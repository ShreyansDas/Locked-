using Unity.Netcode.Components;

namespace Locked
{
    /// <summary>
    /// Owner-authoritative transform: each client moves its own character for zero-latency feel.
    /// Fine for the MVP; move to server-authoritative + prediction before any public/ranked release.
    /// </summary>
    public class ClientNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative() => false;
    }
}
