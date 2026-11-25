namespace JXReborn.WireParityProxy.ShadowStates;

/// <summary>
/// Tracks a ground object observed via <c>0x65</c> (<c>OBJ_SYNC_STATE</c>), <c>0x64</c> (<c>s2c_objadd</c>),
/// and <c>0x67</c> (<c>s2c_objremove</c>) for cross-backend <c>C2S 0x4A</c> translation.
/// </summary>
public readonly record struct ShadowGroundObject(
    int ObjId,
    byte State,
    int MpsX,
    int MpsY,
    bool HasCoords,
    int FirstSeenOrder,
    bool IsMoney = false,
    string NameUtf8 = "");
