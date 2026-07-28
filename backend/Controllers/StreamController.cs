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
        static StreamController()
        {
            FFmpegInit.Initialise();
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
            string rtspURL = "";

            var ffmpegSource = new FFmpegFileSource(rtspURL, false, null);
            var videoTrack = new MediaStreamTrack(ffmpegSource.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly);
            pc.addTrack(videoTrack);

            // Wire up the encoded video samples from FFmpeg to the WebRTC connection
            ffmpegSource.OnVideoSourceEncodedSample += pc.SendVideo;

            // 4. Handle the WebRTC Handshake (Signaling)
            var offerInit = new RTCSessionDescriptionInit { sdp = offerSdp, type = RTCSdpType.offer };
            pc.setRemoteDescription(offerInit);

            var answer = pc.createAnswer(null);
            await pc.setLocalDescription(answer);

            // 5. Connection lifecycle management
            pc.onconnectionstatechange += (state) =>
            {
                if (state == RTCPeerConnectionState.closed || state == RTCPeerConnectionState.failed)
                {
                    ffmpegSource.CloseVideo();
                    pc.Close("Client disconnected");
                }
                else if (state == RTCPeerConnectionState.connected)
                {
                    // Start pulling the RTSP stream once the browser connects
                    _ = Task.Run(() => ffmpegSource.StartVideo());
                }
            };

            // WHEP requires a 201 Created response with SDP answer in the body
            Response.ContentType = "application/sdp";
            return Created(string.Empty, pc.localDescription.sdp);
        }


    }
}
