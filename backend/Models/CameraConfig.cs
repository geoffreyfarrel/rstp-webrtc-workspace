namespace backend.Models
{
    // RtspTransport: optional override for libavformat's RTSP transport
    // negotiation ("tcp" or "udp"). Left null, FFmpeg's default applies
    // (tries UDP, falls back to TCP-interleaved on failure/timeout) - only
    // set this for cameras/networks where that auto-negotiation doesn't
    // work and TCP (or UDP) needs to be forced.
    public record CameraConfig(string Id, string Name, string RtspUrl, string? RtspTransport = null);
}
