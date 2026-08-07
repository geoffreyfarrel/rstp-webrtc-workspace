using System.Reflection;
using backend.Models;
using backend.Services;
using Microsoft.AspNetCore.Mvc;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace backend.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class StreamController : ControllerBase
    {
        private readonly ILogger<StreamController> _logger;
        private readonly IConfiguration _configuration;

        public StreamController(ILogger<StreamController> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public static void Initialise(string contentRootPath, string relativeLibPath)
        {
            string binaryPath = Path.Combine(contentRootPath, relativeLibPath);
            FFmpegInit.Initialise(libPath: binaryPath);
        }

        [HttpGet("test")]
        public IActionResult GetTest()
        {
            return Ok("Backend is reachable!");
        }

        private List<CameraConfig> ResolveCameras() =>
            _configuration.GetSection("StreamSettings:Cameras").Get<List<CameraConfig>>() ?? [];

        // Never returns RtspUrl - camera credentials stay server-side, the
        // browser only ever needs an id to request a stream by.
        [HttpGet("cameras")]
        public IActionResult GetCameras()
        {
            var cameras = ResolveCameras().Select(c => new { c.Id, c.Name });
            return Ok(cameras);
        }

        // Time-limited TURN credentials (coturn's use-auth-secret scheme) for
        // the browser's own ICE side of the connection - the backend mints
        // these instead of handing out a static username/password so the
        // shared secret in appsettings.json never reaches client code.
        [HttpGet("turn-credentials/{cameraId}")]
        public IActionResult GetTurnCredentials(string cameraId)
        {
            var secret = _configuration["Turn:Secret"];
            if (string.IsNullOrEmpty(secret))
            {
                _logger.LogError("[Config] 'Turn:Secret' is not set in appsettings.json");
                return StatusCode(StatusCodes.Status500InternalServerError, "TURN secret is not configured.");
            }

            var (username, credential) = TurnCredentialGenerator.Generate(secret, cameraId, TimeSpan.FromHours(1));
            return Ok(new { host = _configuration["Turn:Host"], username, credential });
        }

        [HttpPost("whep/{cameraId}")]
        public async Task<IActionResult> PostWhepOffer(string cameraId)
        {
            var camera = ResolveCameras().FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));
            if (camera == null)
            {
                _logger.LogError("[Config] No camera configured with id '{CameraId}'", cameraId);
                return NotFound($"No camera configured with id '{cameraId}'.");
            }

            void LogInfo(string message, params object?[] args) => _logger.LogInformation($"[{cameraId}] {message}", args);
            void LogWarn(string message, params object?[] args) => _logger.LogWarning($"[{cameraId}] {message}", args);
            void LogErr(string message, params object?[] args) => _logger.LogError($"[{cameraId}] {message}", args);
            void LogErrEx(Exception ex, string message, params object?[] args) => _logger.LogError(ex, $"[{cameraId}] {message}", args);

            // 1. Read the SDP offer from the frontend's POST request body
            using var reader = new StreamReader(Request.Body);
            var offerSdp = await reader.ReadToEndAsync();
            if (string.IsNullOrEmpty(offerSdp))
            {
                return BadRequest("No SDP offer provided.");
            }

            // NOTE: don't try to strip the offer's audio m-line to sidestep
            // SIPSorcery's handling of it - tried that, and it breaks a
            // different rule instead: the answer must have the same number
            // and order of m-lines as the offer the browser itself sent
            // (RFC 3264/JSEP), so Chrome rejected the answer with "The order
            // of m-lines in answer doesn't match order in offer." An
            // unwanted m-line must be answered as inactive/rejected, not
            // omitted.
            LogInfo("[PC] Offer SDP (raw):\n{Sdp}", offerSdp);

            // 2. Initialize a new WebRTC Peer Connection (STUN fallback + our TURN relay)
            var turnSecret = _configuration["Turn:Secret"];
            if (string.IsNullOrEmpty(turnSecret))
            {
                LogErr("[Config] 'Turn:Secret' is not set in appsettings.json");
                return StatusCode(StatusCodes.Status500InternalServerError, "TURN secret is not configured.");
            }
            // Own time-limited credential, distinct per camera/negotiation - each
            // RTCPeerConnection (this one and the browser's, via GET
            // turn-credentials/{cameraId}) gets its own identity instead of every
            // camera/client sharing one static TURN user.
            var (backendTurnUsername, backendTurnCredential) =
                TurnCredentialGenerator.Generate(turnSecret, $"backend-{cameraId}", TimeSpan.FromHours(1));

            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    // Same coturn box also answers plain STUN binding requests - lets ICE
                    // learn the server-reflexive candidate via a lightweight STUN Binding
                    // request instead of always needing a full TURN Allocate.
                    new RTCIceServer { urls = $"stun:{_configuration["Turn:Host"]}" },
                    new RTCIceServer
                    {
                        urls = $"turn:{_configuration["Turn:Host"]}",
                        username = backendTurnUsername,
                        credential = backendTurnCredential,
                    },
                },
                X_BindAddress = System.Net.IPAddress.Any,
            };
            var pc = new RTCPeerConnection(config);

            // 3. Set up the RTSP Video Source using FFmpeg
            var rtspURL = camera.RtspUrl;
            LogInfo("[Config] Using RTSP URL for '{CameraName}': {Url}", camera.Name,
                rtspURL.Contains('@') ? rtspURL[(rtspURL.IndexOf('@'))..] : rtspURL); // avoid logging credentials

            var ffmpegSource = new FFmpegFileSource(rtspURL, false, null);

            // Restrict to a specific format the encoder can actually produce
            ffmpegSource.RestrictFormats(format => format.Codec == VideoCodecsEnum.H264);

            // not necessary
            var ffmpegInitLock = new SemaphoreSlim(1, 1);
            var videoFormatSet = false;

            try
            {
                var videoFormats = ffmpegSource.GetVideoSourceFormats();

                LogInfo("[FFmpeg] Video formats found: {Count} — {Formats}",
                    videoFormats.Count,
                    string.Join(", ", videoFormats.Select(f => f.Codec.ToString())));

                var videoTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.SendOnly);
                pc.addTrack(videoTrack);

                // Bandwidth for the TURN-relay test is minimized via the encoder bitrate cap
                // alone (see the PRODUCTION TUNING block below). An earlier attempt to also
                // drop most encoded samples here broke H264's inter-frame prediction chain -
                // only the very first sample is a true self-contained IDR, so skipping frames
                // after it (P-frames, or "keyframes" libx264 silently downgrades to non-IDR
                // I-frames) left the decoder permanently out of sync and nothing ever
                // rendered. Every encoded sample must be forwarded - if framerate ever needs
                // throttling, do it on the *raw* frames in OnVideoSourceRawSample below
                // instead, not here.

                // Wire up the encoded video samples from FFmpeg to the WebRTC connection
                //
                // NOTE: these fire ~20-30x/sec per camera - LogInformation here (piped
                // synchronously to console + backend.log) floods the logger badly enough
                // that a second camera negotiating ICE while this one is already streaming
                // sees its STUN retransmission timers starved and every checklist entry
                // times out. LogDebug so they're filtered out at the default Information
                // level but can still be re-enabled for debugging.
                ffmpegSource.OnVideoSourceEncodedSample += (durationRtpUnits, sample) =>
                {
                    _logger.LogDebug("[{CameraId}] [FFmpeg] Sending sample, {Bytes} bytes", cameraId, sample.Length);
                    pc.SendVideo(durationRtpUnits, sample);
                };

                ffmpegSource.OnVideoSourceRawSample += (durationMs, width, height, sample, pixelFormat) =>
                {
                    _logger.LogDebug("[{CameraId}] [FFmpeg] Raw sample: {Width}x{Height}, {Bytes} bytes", cameraId, width, height, sample.Length);
                };

                // CRITICAL: tells the source which format was actually negotiated with the
                // browser once SDP negotiation completes. Without this, the source never
                // knows what to encode into and silently produces no samples.
                //
                // This now runs under ffmpegInitLock so it can never overlap with Start().
                pc.OnVideoFormatsNegotiated += (negotiatedFormats) =>
                {
                    var chosenFormat = negotiatedFormats.First();
                    LogInfo("[FFmpeg] Video format negotiated: {Format}", chosenFormat.Codec);

                    _ = Task.Run(async () =>
                    {
                        await ffmpegInitLock.WaitAsync();
                        try
                        {
                            // Real cameras (RTSP/TCP handshake) can take a while here.
                            // No artificial timeout — let it run, but the lock guarantees
                            // Start() will simply wait rather than colliding with it.
                            ffmpegSource.SetVideoSourceFormat(chosenFormat);
                            videoFormatSet = true;
                            LogInfo("[FFmpeg] SetVideoSourceFormat completed OK");

                            // ================================================================
                            // PRODUCTION TUNING: bitrate & framerate - adjust here.
                            //
                            // Bitrate: set via StreamSettings:TargetBitrate in
                            // appsettings.json (100kbps below is a fallback default). It was
                            // picked deliberately low to keep the TURN relay test lightweight,
                            // not for real-world video quality - raise it for production.
                            //
                            // Framerate: not throttled anywhere currently - every raw frame
                            // from the camera gets encoded and forwarded as-is. If it ever
                            // needs limiting, do it in OnVideoSourceRawSample above (skip raw
                            // frames before they're encoded), never on already-encoded samples
                            // in OnVideoSourceEncodedSample - that breaks H264's inter-frame
                            // prediction chain (see the comment above that handler).
                            // ================================================================
                            //
                            // SIPSorceryMedia.FFmpeg doesn't expose bitrate control on
                            // FFmpegFileSource itself, only on the internal FFmpegVideoSource
                            // it wraps (private field). Reached via reflection below. NOTE:
                            // relies on the "_FFmpegVideoSource" private field name in the
                            // SIPSorceryMedia.FFmpeg NuGet package (v10.0.12) — re-check this if
                            // that package is upgraded.
                            var targetBitrate = _configuration.GetValue<long?>("StreamSettings:TargetBitrate") ?? 100_000;
                            var videoSourceField = typeof(FFmpegFileSource).GetField(
                                "_FFmpegVideoSource", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (videoSourceField?.GetValue(ffmpegSource) is FFmpegVideoSource innerVideoSource)
                            {
                                innerVideoSource.SetVideoEncoderBitrate(targetBitrate, null, null, targetBitrate);
                                LogInfo("[FFmpeg] Capped encoder bitrate to {Bitrate} bps", targetBitrate);
                            }
                            else
                            {
                                LogWarn("[FFmpeg] Could not reach internal FFmpegVideoSource via reflection; bitrate not capped.");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogErrEx(ex, "[FFmpeg] SetVideoSourceFormat threw");
                        }
                        finally
                        {
                            ffmpegInitLock.Release();
                        }
                    });
                };

                // 4. Handle the WebRTC Handshake (Signaling)
                var offerInit = new RTCSessionDescriptionInit { sdp = offerSdp, type = RTCSdpType.offer };
                var setResult = pc.setRemoteDescription(offerInit);
                if (setResult != SetDescriptionResultEnum.OK)
                {
                    LogErr("[PC] setRemoteDescription failed: {Result}", setResult);
                    return BadRequest($"Failed to set remote description: {setResult}");
                }

                var answer = pc.createAnswer(null);
                await pc.setLocalDescription(answer);

                // Wait for ICE gathering to complete so the SDP answer includes all candidates
                var iceGatheringComplete = new TaskCompletionSource<bool>();
                pc.onicegatheringstatechange += (state) =>
                {
                    LogInfo("[PC] ICE gathering state: {State}", state);
                    if (state == RTCIceGatheringState.complete)
                    {
                        iceGatheringComplete.TrySetResult(true);
                    }
                };
                if (pc.iceGatheringState == RTCIceGatheringState.complete)
                {
                    iceGatheringComplete.TrySetResult(true);
                }

                var completedTask = await Task.WhenAny(iceGatheringComplete.Task, Task.Delay(120000));
                if (completedTask != iceGatheringComplete.Task)
                {
                    LogWarn("[PC] ICE gathering did not complete within 120s, returning SDP anyway.");
                }

                // 5. Connection lifecycle management
                pc.onconnectionstatechange += (state) =>
                {
                    LogInfo("[PC] Connection state: {State}", state);

                    if (state == RTCPeerConnectionState.closed || state == RTCPeerConnectionState.failed)
                    {
                        ffmpegSource.CloseVideo();
                        pc.Close("Client disconnected");
                    }
                    else if (state == RTCPeerConnectionState.connected)
                    {
                        _ = Task.Run(async () =>
                        {
                            await ffmpegInitLock.WaitAsync();
                            try
                            {
                                if (!videoFormatSet)
                                {
                                    LogWarn("[FFmpeg] Connection state is 'connected' but video format " +
                                        "hasn't finished being set yet — waited for lock, proceeding now.");
                                }

                                LogInfo("[FFmpeg] Starting video source...");

                                var startTask = ffmpegSource.Start();
                                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(20));

                                var raceResult = await Task.WhenAny(startTask, timeoutTask);

                                if (raceResult == timeoutTask)
                                {
                                    LogErr("[ffmpeg] RTSP connection timed out after 120s. Closing connection.");
                                    await ffmpegSource.CloseVideo();
                                    pc.Close("RTSP source unreachable (timeout)");
                                }
                                else
                                {
                                    // startTask finished first — but check if it actually succeeded or threw
                                    await startTask;
                                    LogInfo("[ffmpeg] Start() completed successfully.");
                                }

                                LogInfo("[FFmpeg] Start() returned normally.");
                            }
                            catch (Exception ex)
                            {
                                LogErrEx(ex, "[FFmpeg] Start() threw an exception.");
                            }
                            finally
                            {
                                ffmpegInitLock.Release();
                            }
                        });
                    }
                };

                pc.oniceconnectionstatechange += (state) =>
                {
                    LogInfo("[PC] ICE connection state: {State}", state);
                };
                var gatheredCandidates = new List<string>();
                pc.onicecandidate += (candidate) =>
                {
                    LogInfo("[PC] Local ICE candidate: {Candidate}", candidate.candidate);
                    if (!string.IsNullOrEmpty(candidate.candidate))
                    {
                        gatheredCandidates.Add(candidate.candidate);
                    }
                };

                var finalSdp = pc.localDescription.sdp.ToString();

                // WORKAROUND: SIPSorcery's SDP renderer only ever embeds a subset of the
                // gathered ICE candidates into pc.localDescription.sdp (observed: only the
                // host candidate, even though srflx/relay candidates were gathered and fired
                // through onicecandidate above) - and even that subset only lands on one
                // m-line. Scanning the SDP text for "a=candidate:" lines therefore only ever
                // recovers the host candidate and silently drops the TURN relay candidate,
                // which is the one that actually matters for any client not on the same LAN.
                // Build the candidate lines from what onicecandidate actually observed instead
                // of trusting the SDP SIPSorcery rendered, then stamp that full set into every
                // m= section (replacing whatever partial set SIPSorcery put there) so all
                // m-lines carry the same complete candidate set for non-trickle ICE.
                var candidateLines = gatheredCandidates
                    .Distinct()
                    .Select(c => $"a=candidate:{c}")
                    .ToList();

                LogInfo("[PC] Found {Count} unique candidate lines to propagate: {Lines}",
                    candidateLines.Count, string.Join(" | ", candidateLines));

                if (candidateLines.Count > 0)
                {
                    var normalizedSdp = finalSdp.Replace("\r\n", "\n");
                    var sections = normalizedSdp.Split(new[] { "\nm=" }, StringSplitOptions.None);
                    for (int i = 1; i < sections.Length; i++) // skip session-level header at index 0
                    {
                        var lines = sections[i]
                            .Split('\n')
                            .Where(l => !l.TrimStart().StartsWith("a=candidate:"))
                            .ToList();
                        int insertAt = lines.FindIndex(l => l.TrimStart().StartsWith("a=ice-pwd:"));
                        if (insertAt == -1) insertAt = lines.Count - 1;
                        lines.InsertRange(insertAt + 1, candidateLines);
                        sections[i] = string.Join("\n", lines);
                    }
                    // NOTE: string.Join only inserts its separator *between*
                    // elements, never before the first one - `sections[0] +
                    // string.Join("\nm=", sections.Skip(1))` was
                    // concatenating the header directly onto the first m=
                    // section with no newline (e.g. "...BUNDLE 0video 9
                    // ..."), corrupting the SDP on every response that had
                    // any candidates to propagate. The leading "\nm=" below
                    // fixes that.
                    finalSdp = sections[0] + "\nm=" + string.Join("\nm=", sections.Skip(1));
                    finalSdp = finalSdp.Replace("\n", "\r\n");
                }

                var candidateCount = finalSdp.Split(new[] { "a=candidate:" }, StringSplitOptions.None).Length - 1;
                LogInfo("[PC] Returning answer with {Count} total candidate lines (after propagation fix). iceGatheringState={State}", candidateCount, pc.iceGatheringState);
                LogInfo("[PC] Answer SDP (final):\n{Sdp}", finalSdp);

                // WHEP requires a 201 Created response with SDP answer in the body
                Response.StatusCode = StatusCodes.Status201Created;

                return Content(finalSdp, "application/sdp");
            }
            catch (Exception ex)
            {
                LogErrEx(ex, "[PC] Exception during WHEP negotiation.");

                await ffmpegSource.CloseVideo();
                pc.Close("Negotiation failed");

                return StatusCode(StatusCodes.Status500InternalServerError, "Failed to negotiate WebRTC connection.");
            }
        }
    }
}
