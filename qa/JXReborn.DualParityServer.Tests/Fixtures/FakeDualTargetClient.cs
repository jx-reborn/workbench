using JXReborn.ClientDriver;
using JXReborn.DualParityServer.Session;

namespace JXReborn.DualParityServer.Tests.Fixtures;

internal sealed class FakeDualTargetClient : IDualTargetClient
{
    private WorldDomSnapshot _state;
    private readonly Dictionary<uint, DialogSnapshot> _npcDialogs = [];

    public FakeDualTargetClient(string targetName, WorldDomSnapshot initialState)
    {
        TargetName = targetName;
        PipeName = $"fake-{targetName}";
        _state = initialState;
    }

    public string TargetName { get; }

    public string PipeName { get; }

    public int? ProcessId => 9999;

    public List<string> RecordedCalls { get; } = [];

    public void SetNpcDialog(uint npcDwId, DialogSnapshot dialog) => _npcDialogs[npcDwId] = dialog;

    public void UpdateState(WorldDomSnapshot state) => _state = state;

    public Task<WorldDomSnapshot> GetStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_state);

    public Task MoveToAsync(int mpsX, int mpsY, bool run = true, CancellationToken cancellationToken = default)
    {
        RecordedCalls.Add($"MoveTo({mpsX},{mpsY})");
        _state = _state with { Player = _state.Player with { MpsX = mpsX, MpsY = mpsY } };
        return Task.CompletedTask;
    }

    public Task InteractNpcByDwIdAsync(uint npcDwId, CancellationToken cancellationToken = default)
    {
        RecordedCalls.Add($"InteractNpc({npcDwId})");
        if (_npcDialogs.TryGetValue(npcDwId, out DialogSnapshot? dlg))
        {
            _state = _state with { Dialog = dlg };
        }

        return Task.CompletedTask;
    }

    public Task SelectDialogAsync(int optionIndex, CancellationToken cancellationToken = default)
    {
        RecordedCalls.Add($"SelectDialog({optionIndex})");
        _state = _state with
        {
            Dialog = new DialogSnapshot(false, _state.Dialog.Sequence + 1, 0, string.Empty, [])
        };
        return Task.CompletedTask;
    }

    public Task ConfirmTalkAsync(CancellationToken cancellationToken = default)
    {
        RecordedCalls.Add("ConfirmTalk()");
        _state = _state with
        {
            Dialog = new DialogSnapshot(false, _state.Dialog.Sequence + 1, 0, string.Empty, [])
        };
        return Task.CompletedTask;
    }

    public Task UseItemAsync(int arrayIndex, int place, int gridX, int gridY, CancellationToken cancellationToken = default)
    {
        RecordedCalls.Add($"UseItem({arrayIndex},{place},{gridX},{gridY})");
        return Task.CompletedTask;
    }

    public Task SwitchPkAsync(int pkMode, CancellationToken cancellationToken = default)
    {
        RecordedCalls.Add($"SwitchPk({pkMode})");
        _state = _state with { Player = _state.Player with { PkFlag = pkMode } };
        return Task.CompletedTask;
    }

    public Task SendChatAsync(int channel, string messageUnicode, CancellationToken cancellationToken = default)
    {
        RecordedCalls.Add($"SendChat({channel},{messageUnicode})");
        return Task.CompletedTask;
    }

    public Task<string?> TryCaptureScreenshotAsync(string outputPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
