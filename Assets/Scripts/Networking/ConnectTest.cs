using UnityEngine;

/// <summary>
/// DEPRECATED / inert. Superseded by TransportSwitcherUI (transport picker + Host/Join + Disconnect).
/// Its old OnGUI connect UI was removed because it drew in the same top-left corner as TransportSwitcherUI
/// and overlapped its buttons (ate the Disconnect click). Kept as an EMPTY component only so any existing
/// scene reference to this script doesn't become a "missing script". Safe to remove this component from its
/// GameObject in the Editor and delete this file.
/// </summary>
public class ConnectTest : MonoBehaviour
{
}
