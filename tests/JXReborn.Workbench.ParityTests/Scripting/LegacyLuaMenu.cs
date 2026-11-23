using System.Collections.Generic;

namespace JXReborn.ZoneEngine.Tests.Scripting;

internal sealed record LegacyLuaMenu(string Prompt, int DeclaredCount, IReadOnlyList<string> Options);
