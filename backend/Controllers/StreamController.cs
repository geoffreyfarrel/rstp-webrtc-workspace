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

        public StreamController(ILogger<StreamController> logger)
        {
            _logger = logger;
        }

        static StreamController()
        {
            string binaryPath = "D:\\Repos\\rtsp-webrtc-workspace\\backend\\public\\bin";
            FFmpegInit.Initialise(libPath: binaryPath);
        }

        [HttpGet("test")]
        public IActionResult GetTest()
        {
            return Ok("Backend is reachable!");
        }

        [HttpPost("whep")]
        public async Task<IActionResult> PostWhepOffer()
        {
            // 1. Read the SDP offer from the frontend's POST request body
            using var reader = new StreamReader(Request.Body);
            var offerSdp = await reader.ReadToEndAsync();
            if (string.IsNullOrEmpty(offerSdp))
            {
                return BadRequest("No SDP offer provided.");
            }

            // 2. Initialize a new WebRTC Peer Connection
            var pc = new RTCPeerConnection(null);

            // 3. Set up the RTSP Video Source using FFmpeg
            string rtspURL = "rtsp://127.0.0.1:8554/mystream";
            var ffmpegSource = new FFmpegFileSource(rtspURL, false, null);

            // Restrict to a specific format the encoder can actually produce
            ffmpegSource.RestrictFormats(format => format.Codec == VideoCodecsEnum.H264);

            try
            {
                var videoFormats = ffmpegSource.GetVideoSourceFormats();
                _logger.LogInformation("[FFmpeg] Video formats found: {Count} — {Formats}",
                    videoFormats.Count,
                    string.Join(", ", videoFormats.Select(f => f.Codec.ToString())));

                var videoTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.SendOnly);
                pc.addTrack(videoTrack);

                // Wire up the encoded video samples from FFmpeg to the WebRTC connection
                ffmpegSource.OnVideoSourceEncodedSample += (durationRtpUnits, sample) =>
                {
                    _logger.LogInformation("[FFmpeg] Sending sample, {Bytes} bytes", sample.Length);
                    pc.SendVideo(durationRtpUnits, sample);
                };

                ffmpegSource.OnVideoSourceRawSample += (durationMs, width, height, sample, pixelFormat) =>
                {
                    _logger.LogInformation("[FFmpeg] Raw sample: {Width}x{Height}, {Bytes} bytes", width, height, sample.Length);
                };

                // CRITICAL: tells the source which format was actually negotiated with the
                // browser once SDP negotiation completes. Without this, the source never
                // knows what to encode into and silently produces no samples.
                pc.OnVideoFormatsNegotiated += (negotiatedFormats) =>
                {
                    var chosenFormat = negotiatedFormats.First();
                    _logger.LogInformation("[FFmpeg] Video format negotiated: {Format}", chosenFormat.Codec);
                    ffmpegSource.SetVideoSourceFormat(chosenFormat);
                };

                // 4. Handle the WebRTC Handshake (Signaling)
                var offerInit = new RTCSessionDescriptionInit { sdp = offerSdp, type = RTCSdpType.offer };
                var setResult = pc.setRemoteDescription(offerInit);
                if (setResult != SetDescriptionResultEnum.OK)
                {
                    _logger.LogError("[PC] setRemoteDescription failed: {Result}", setResult);
                    return BadRequest($"Failed to set remote description: {setResult}");
                }

                var answer = pc.createAnswer(null);
                await pc.setLocalDescription(answer);

                // Wait for ICE gathering to complete so the SDP answer includes all candidates
                var iceGatheringComplete = new TaskCompletionSource<bool>();
                pc.onicegatheringstatechange += (state) =>
                {
                    _logger.LogInformation("[PC] ICE gathering state: {State}", state);
                    if (state == RTCIceGatheringState.complete)
                    {
                        iceGatheringComplete.TrySetResult(true);
                    }
                };
                if (pc.iceGatheringState == RTCIceGatheringState.complete)
                {
                    iceGatheringComplete.TrySetResult(true);
                }

                var completedTask = await Task.WhenAny(iceGatheringComplete.Task, Task.Delay(5000));
                if (completedTask != iceGatheringComplete.Task)
                {
                    _logger.LogWarning("[PC] ICE gathering did not complete within 5s, returning SDP anyway.");
                }

                // 5. Connection lifecycle management
                pc.onconnectionstatechange += (state) =>
                {
                    _logger.LogInformation("[PC] Connection state: {State}", state);

                    if (state == RTCPeerConnectionState.closed || state == RTCPeerConnectionState.failed)
                    {
                        ffmpegSource.CloseVideo();
                        pc.Close("Client disconnected");
                    }
                    else if (state == RTCPeerConnectionState.connected)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                _logger.LogInformation("[FFmpeg] Starting video source...");
                                await ffmpegSource.Start();
                                _logger.LogInformation("[FFmpeg] Start() returned normally.");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[FFmpeg] Start() threw an exception.");
                            }
                        });
                    }
                };

                pc.oniceconnectionstatechange += (state) =>
                {
                    _logger.LogInformation("[PC] ICE connection state: {State}", state);
                };

                // WHEP requires a 201 Created response with SDP answer in the body
                Response.StatusCode = StatusCodes.Status201Created;
                return Content(pc.localDescription.sdp.ToString(), "application/sdp");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PC] Exception during WHEP negotiation.");
                await ffmpegSource.CloseVideo();
                pc.Close("Negotiation failed");
                return StatusCode(StatusCodes.Status500InternalServerError, "Failed to negotiate WebRTC connection.");
            }
        }
    }
}