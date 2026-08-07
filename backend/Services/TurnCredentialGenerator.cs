using System.Security.Cryptography;
using System.Text;

namespace backend.Services
{
    // coturn's TURN REST API / time-limited-credential scheme
    // (use-auth-secret): username is "<unix-expiry>:<label>", credential is
    // HMAC-SHA1(secret, username) base64-encoded. Lets any number of
    // clients (backend or browser, any camera) mint their own valid,
    // expiring TURN credentials from a single shared secret - no per-user
    // provisioning on the coturn side when a camera is added.
    public static class TurnCredentialGenerator
    {
        public static (string Username, string Credential) Generate(string secret, string label, TimeSpan ttl)
        {
            var expiry = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
            var username = $"{expiry}:{label}";

            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(username));
            var credential = Convert.ToBase64String(hash);

            return (username, credential);
        }
    }
}
