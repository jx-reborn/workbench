using System.Threading;
using System.Threading.Tasks;
using JXReborn.ZoneEngine.Scripting;

namespace JXReborn.ScriptMigrator.Tests.Verification;

internal sealed class SabotagedNpcDialogScript : INpcDialogScript
{
    public string ScriptCode => "test-sabotaged-npc-dialog";

    public ValueTask OnDialogAsync(IScriptDialogContext context, CancellationToken cancellationToken = default)
    {
        // Deliberately mutate task 999 and alter dialog text so differential oracle MUST catch the divergence
        context.SetTask(999, 777);
        context.Say("Chuỗi thoại bị làm sai lệch so với Lua 4.0 gốc");
        return ValueTask.CompletedTask;
    }
}
