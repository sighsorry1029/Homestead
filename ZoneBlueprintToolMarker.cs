using UnityEngine;

namespace Homestead;

internal sealed class ZoneBlueprintSaveToolMarker : MonoBehaviour
{
    public ZoneBlueprintToolKind Kind;
    public string BlueprintName = "";
}

internal enum ZoneBlueprintToolKind
{
    AreaSave,
    AreaDismantle,
    BlueprintSnapPoint,
    Blueprint,
    Store,
    DataFolder
}
