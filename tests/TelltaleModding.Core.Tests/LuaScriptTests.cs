using System.Text;
using TelltaleModding.Core.Scripts;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// Lua script reading, disassembly and decompilation. Game samples are real scripts from
/// Tales from the Borderlands, Game of Thrones and The Walking Dead Definitive Series
/// (Lua 5.2) and The Wolf Among Us (Lua 5.1 with Telltale's opcode numbering); the
/// <c>script_stock5x_corpus</c> files were compiled by stock Lua 5.1/5.2 (8-byte numbers,
/// 64-bit sizes) from a test program covering most of the language.
/// </summary>
public class LuaScriptTests
{
    private static LuaScript Load(string sample) =>
        LuaScriptReader.Read(File.ReadAllBytes(SampleData.Sample(sample)), sample);

    [SkippableTheory]
    [InlineData("foo.lua", true)]
    [InlineData("Foo.LENC", true)]
    [InlineData("x.luac", true)]
    [InlineData("x.lua.bak", false)]
    [InlineData("x.d3dtx", false)]
    public void Recognises_script_extensions(string name, bool expected) =>
        Assert.Equal(expected, LuaScriptReader.IsScriptFile(name));

    [SampleFact("resdesc_wdc_WalkingDead401.lua")]
    public void Plain_source_passes_through_unchanged()
    {
        var bytes = File.ReadAllBytes(SampleData.Sample("resdesc_wdc_WalkingDead401.lua"));
        var script = LuaScriptReader.Read(bytes, "resdesc_wdc_WalkingDead401.lua");
        Assert.Equal(LuaScriptKind.Source, script.Kind);
        Assert.Equal("Lua source", script.VersionLabel);
        Assert.Equal(Encoding.UTF8.GetString(bytes), script.Decompiled);
        Assert.Equal("", script.Disassembly);
        Assert.Equal(0, script.DecompileFallbacks);
        Assert.Contains("WalkingDead401", script.StringConstants);
    }

    [SampleFact("resdesc_wdc_PC_P01_WalkingDead401_encrypted.lua")]
    public void Still_encrypted_script_is_unknown_with_a_preview()
    {
        var script = Load("resdesc_wdc_PC_P01_WalkingDead401_encrypted.lua");
        Assert.Equal(LuaScriptKind.Unknown, script.Kind);
        Assert.Contains("encrypted", script.Decompiled);
        Assert.Contains("1B 4C 45 6F", script.Decompiled);
    }

    [SampleFact("resdesc_got_DT_101_P.luac")]
    public void Lua52_resource_description_decompiles_to_its_assignments()
    {
        var script = Load("resdesc_got_DT_101_P.luac");
        Assert.Equal(LuaScriptKind.Bytecode52, script.Kind);
        Assert.Equal("Lua 5.2 bytecode", script.VersionLabel);
        Assert.EndsWith("_resdesc_50_dt_101_p.lua", script.SourceName);
        Assert.Equal(0, script.DecompileFallbacks);
        var text = script.Decompiled;
        Assert.Contains("local set = {}", text);
        Assert.Contains("set.name = \"DT_101_P\"", text);
        Assert.Contains("set.priority = 280", text);
        Assert.Contains("set.localDirExclude = {\"_dev/\"}", text);
        Assert.Contains("set.gameDataArchives = {_currentDirectory .. \"GameOfThrones_pc_DT_101_P_all.ttarch2\"}", text);
        Assert.Contains("RegisterSetDescription(set)", text);
    }

    [SampleFact("resdesc_wd2_Menu.luac")]
    public void Lua51_resource_description_uses_the_source_name_from_debug_info()
    {
        var script = Load("resdesc_wd2_Menu.luac");
        Assert.Equal(LuaScriptKind.Bytecode51, script.Kind);
        Assert.Equal("C:/Users/Mark Gabby/Desktop/LENC/_resdesc_50_Menu.lua", script.SourceName);
        Assert.Contains("set.enableMode = \"bootable\"", script.Decompiled);
        Assert.Contains("_currentDirectory .. \"WalkingDead_pc_Menu_data.ttarch2\"", script.Decompiled);
    }

    [SampleFact("script_got_AchievementManager.lua")]
    public void Game_of_Thrones_script_decompiles_to_readable_source()
    {
        var script = Load("script_got_AchievementManager.lua");
        Assert.Equal(LuaScriptKind.Bytecode52, script.Kind);
        Assert.Equal(0, script.DecompileFallbacks);
        var text = script.Decompiled;
        Assert.Contains("local function getid(ach)", text);
        Assert.Contains("    elseif IsEngineXbox360() then\n      return tonumber(ach.Xbox360)", text);
        Assert.Contains("if mAchievements == nil or mbNeedToUpdate then", text);
        Assert.Contains("for _, key in ipairs(PropertyGetKeys(kAchievementIDsProp)) do", text);
        Assert.Contains("function AchievementManager_Unlock(ach)", text);
        Assert.Contains("return achdata and achdata.achieved", text);
        Assert.Equal(new[] { "PrepareAssets", "getid", "update_achievements", "AchievementManager_Add",
            "AchievementManager_Unlock", "AchievementManager_IsUnlocked" }, script.FunctionNames);
        Assert.Equal(7, script.FunctionCount);
        Assert.Contains("achievement_ids.prop", script.StringConstants);
    }

    [SampleFact("script_wdc_Menu_sellScreen.lua")]
    public void Methods_and_nested_conditions_are_reconstructed()
    {
        var script = Load("script_wdc_Menu_sellScreen.lua");
        Assert.Equal(0, script.DecompileFallbacks);
        var text = script.Decompiled;
        Assert.Contains("function menu:populate()", text);
        Assert.Contains("local bannerVisible = {false, false, false, false, false}", text);
        Assert.Contains("if episode == 0 and not bannerVisible[i] or i == episode and not bannerVisible[i] then", text);
        Assert.Contains("menu:populate", script.FunctionNames);
    }

    [SampleFact("script_tftb_OldHaven.lua")]
    public void Small_scene_script_matches_the_expected_source()
    {
        var text = Load("script_tftb_OldHaven.lua").Decompiled;
        Assert.EndsWith(
            "local kScene = \"adv_oldHaven.scene\"\nlocal kScript = \"OldHaven\"\n\n" +
            "function OldHaven()\n  Game_NewScene(kScene, kScript)\n  Game_StartScene(true)\nend\n\n" +
            "SceneOpen(kScene, kScript)\n", text);
    }

    [SampleFact("script_wau_AchievementManager.lenc")]
    public void Wolf_Among_Us_scripts_use_telltales_opcode_numbering()
    {
        var script = Load("script_wau_AchievementManager.lenc");
        Assert.Equal(LuaScriptKind.Bytecode51, script.Kind);
        Assert.True(script.Chunk!.TelltaleOpcodes);
        Assert.True(script.Chunk.SinglePrecision);
        Assert.Equal(0, script.DecompileFallbacks);
        Assert.Contains("Print(\"Enabling \" .. _description.name .. \" Silo\")", script.Decompiled);
        Assert.Contains("function AchievementManager_IsUnlocked(ach)", script.Decompiled);
        Assert.Contains("Telltale opcode numbering", script.Disassembly);
        Assert.Contains("GETGLOBAL", script.Disassembly);
    }

    [SampleFact("script_wau_Tool_LanguageContextualize.lenc")]
    public void Loops_breaks_and_multiple_assignment_in_a_larger_51_script()
    {
        var script = Load("script_wau_Tool_LanguageContextualize.lenc");
        Assert.Equal(0, script.DecompileFallbacks);
        var text = script.Decompiled;
        Assert.Contains("local function ProcessLoop_Forwards(lines, curLine, startNode, numLines)", text);
        Assert.Contains("while nextNode and nextNode.Type ~= \"Wait\" and nextNode.Type ~= \"Cencel Choices\" do", text);
        Assert.Contains("linesFound, curLine = ProcessExchange(lines, curLine, node, numLines, false)", text);
        Assert.Contains("local idx_EndOfPre, idx_EndOfConcurrent", text);
        Assert.Contains("  function LCDPrint(...)\n    Print(\"LangContextDebug - \" .. ...)\n  end\nelse", text);
    }

    [SampleFact("script_stock52_corpus.luac")]
    public void Stock_52_bytecode_with_64_bit_sizes_decompiles()
    {
        var script = Load("script_stock52_corpus.luac");
        Assert.Equal(8, script.Chunk!.SizeOfSizeT);
        Assert.False(script.Chunk.SinglePrecision);
        AssertCorpus(script);
    }

    [SampleFact("script_stock51_corpus.luac")]
    public void Stock_51_bytecode_decompiles()
    {
        var script = Load("script_stock51_corpus.luac");
        Assert.False(script.Chunk!.TelltaleOpcodes);
        AssertCorpus(script);
    }

    private static void AssertCorpus(LuaScript script)
    {
        Assert.Equal(0, script.DecompileFallbacks);
        var text = script.Decompiled;
        string[] expected =
        {
            "local x = a and b or c",
            "local y = a or b and c",
            "local w = (a or b) and c",
            "local s = (a or 0) < 3 and \"small\" or \"big\"",
            "    if i % 15 == 0 then\n      r[#r + 1] = \"FizzBuzz\"\n    elseif i % 3 == 0 then",
            "if a == 1 and (b == 2 or c == 3) and not d then",
            "  repeat\n    local k = j * 2\n    j = j + 1\n  until k >= 6 or j > 20",
            "for x = 10, 1, -3 do",
            "for k, v in pairs(t) do",
            "local a, b = ...",
            "return n, a, b, #all, ...",
            "emit((varargs(9, 8)))",
            "    fns[i] = function(x)\n      return x + i + j\n    end",
            "local function fact(k)",
            "function Account:deposit(v)",
            "acc:deposit(5):deposit(7)",
            "local t = {1, 2, 3, n = \"x\", [10] = \"ten\", [\"with space\"] = true, {nested = {1, 2}}}",
            "local q = {(three())}",
            "  a, b = b, a",
            "t.x, t.y, c = a, b, \"c\"",
            "local u, v, w = 0, two()",
            "\"tab\\there \\\"quoted\\\" back\\\\slash \\001\\002 nl\\n\"",
            "_G[\"odd name\"] = 5",
            "  do\n    local x = 2\n    emit(\"inner\", x)\n  end",
            "while t[i] and t[i] > 0 do",
        };
        foreach (var e in expected)
            Assert.Contains(e, text);
        Assert.Contains("Account:deposit", script.FunctionNames);
        Assert.Contains("fact", script.FunctionNames);
    }

    [SampleFact("script_stock52_corpus_stripped.luac")]
    public void Stripped_bytecode_still_decompiles_without_throwing()
    {
        var script = Load("script_stock52_corpus_stripped.luac");
        Assert.Null(script.SourceName);
        Assert.Contains("local r0, r1, r2", script.Decompiled);
        Assert.Contains("function r1(...)", script.Decompiled);
        Assert.Contains("upvalues:", script.Disassembly);
    }

    [SampleFact("script_got_AchievementManager.lua")]
    public void Disassembly_resolves_constants_and_local_names_with_lines()
    {
        var dis = Load("script_got_AchievementManager.lua").Disassembly;
        Assert.Contains("-- function main/1: getid (lines 80-98)", dis);
        Assert.Contains("--   r0 ach", dis);
        Assert.Matches(@"\[81\]\s+GETTABUP\s+1 0 256\s+; r1 := PropertyExists", dis);
        Assert.Contains("MOVE       3 0 0          ; r3 := ach", dis);
    }

    [SkippableFact]
    public void Garbage_and_truncated_data_never_throw()
    {
        var random = new Random(1234);
        var inputs = new List<byte[]>
        {
            Array.Empty<byte>(),
            new byte[] { 0x1B, (byte)'L', (byte)'u', (byte)'a' },
            new byte[] { 0x1B, (byte)'L', (byte)'u', (byte)'a', 0x53, 0, 1, 4, 8, 4, 8, 0 },
            new byte[] { 0x1B, (byte)'L', (byte)'u', (byte)'a', 0x52, 0, 1, 4, 4, 4, 4, 0 },
            Enumerable.Range(0, 300).Select(_ => (byte)random.Next(256)).ToArray(),
        };
        foreach (var data in inputs)
        {
            var script = LuaScriptReader.Read(data, "x.lua");
            _ = script.Decompiled;
            _ = script.Disassembly;
        }
        Assert.Equal(LuaScriptKind.Unknown, LuaScriptReader.Read(inputs[3], "x.lua").Kind);
    }

    [SampleFact("script_wau_AchievementManager.lenc", "script_got_AchievementManager.lua")]
    public void Damaged_bytecode_never_throws()
    {
        var random = new Random(42);
        foreach (var name in new[] { "script_wau_AchievementManager.lenc", "script_got_AchievementManager.lua" })
        {
            var original = File.ReadAllBytes(SampleData.Sample(name));
            for (int len = 0; len < original.Length; len += 97)
                Touch(LuaScriptReader.Read(original.AsSpan(0, len).ToArray(), name));
            for (int i = 0; i < 150; i++)
            {
                var data = (byte[])original.Clone();
                int flips = 1 + random.Next(8);
                for (int f = 0; f < flips; f++)
                    data[18 + random.Next(data.Length - 18)] = (byte)random.Next(256);
                Touch(LuaScriptReader.Read(data, name));
            }
        }

        static void Touch(LuaScript s)
        {
            _ = s.Decompiled;
            _ = s.Disassembly;
            _ = s.FunctionNames.Count + s.StringConstants.Count + s.DecompileFallbacks;
        }
    }
}
