using Unity.Netcode.Components;
using UnityEngine;

[DisallowMultipleComponent]
public class ClientNetworkTransform : NetworkTransform
{
    // Client-authoritative: the OWNER writes its transform, others read it.
    // This is the whole reason you're not using stock (server-auth) NetworkTransform.
    protected override bool OnIsServerAuthoritative() => false;
}