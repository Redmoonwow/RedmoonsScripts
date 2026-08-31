using Dalamud.Bindings.ImGui;
using Splatoon.SplatoonScripting;
using System.Collections.Generic;

namespace RedmoonsScripts.Tests;

public class SampleTest : SplatoonScript
{
    public override HashSet<uint>? ValidTerritories => null;
    public override Metadata Metadata => new(1, "Redmoon");

    public override void OnSettingsDraw()
    {
        ImGui.Text("RedmoonsScripts build environment is working.");
    }
}
