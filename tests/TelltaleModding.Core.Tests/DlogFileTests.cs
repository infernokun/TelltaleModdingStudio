using TelltaleModding.Core.Dialogue;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Tests;

public class DlogFileTests
{
    private static (DlogFile Dialog, Dictionary<uint, DialogueLine> Lines) Read(string dlog, string landb)
    {
        var data = File.ReadAllBytes(SampleData.Sample(dlog));
        Assert.True(DlogFile.TryRead(data, out var dialog, out var error), error);
        dialog!.ResolveNames(DlogFile.NameCandidates(data));
        Assert.True(LandbFile.TryRead(File.ReadAllBytes(SampleData.Sample(landb)), out var lines, out error), error);
        return (dialog, lines.ToDictionary(l => l.Id));
    }

    private static string Say(Dictionary<uint, DialogueLine> lines, uint id) => $"{lines[id].Speaker}: {lines[id].Text}";

    private static IEnumerable<string> OptionTexts(DlogFile dialog, DlogNode menu, Dictionary<uint, DialogueLine> lines) =>
        menu.Options.Select(o => o.TextIds.Count > 0 ? lines[o.TextIds[0]].Text : "");

    [SkippableFact]
    public void Reads_a_choice_hub_from_the_definitive_series()
    {
        var (dialog, lines) = Read("dlog_wdc_crawfordautoshop_lee_molly.dlog", "landb_wdc_crawfordautoshop_lee_molly_english.landb");

        Assert.Equal("crawfordautoshop_lee_molly.dlog", dialog.Name);
        Assert.Equal(MetaStreamFormat.Msv6, dialog.Format);
        Assert.Equal(19, dialog.Nodes.Count);
        Assert.All(dialog.Nodes, n => Assert.True(n.IsComplete));
        Assert.Equal(0, dialog.SkippedBytes);
        var folder = Assert.Single(dialog.Folders);
        Assert.Equal("Default", folder.Name);
        var conversation = Assert.Single(folder.Conversations);
        Assert.Equal(DlogChildKind.Conversation, conversation.Kind);

        var chain = dialog.Chain(conversation).ToList();
        Assert.Equal(new[] { DlogNodeKind.Start, DlogNodeKind.Exchange, DlogNodeKind.Idle, DlogNodeKind.Choices, DlogNodeKind.Wait },
            chain.Select(n => n.Kind));
        Assert.Equal("MOLLY: Something you need?", Say(lines, Assert.Single(chain[1].LineIds)));

        var menu = chain[3];
        Assert.Equal(new[] { DlogChildKind.PreChoice, DlogChildKind.Choice, DlogChildKind.Choice, DlogChildKind.Choice, DlogChildKind.Choice, DlogChildKind.PostChoice },
            menu.Children.Select(c => c.Kind));
        Assert.Equal(new[] { "So... Crawford...", "About that walker in the alley...", "", "Bye" }, OptionTexts(dialog, menu, lines));
        Assert.Equal(new uint[] { 314699788 }, menu.Options.First().TextIds);

        // The first option plays Lee and Molly's exchange.
        var crawford = dialog.Chain(menu.Options.First()).ToList();
        Assert.Equal(DlogNodeKind.Exchange, Assert.Single(crawford).Kind);
        Assert.Equal(4, crawford[0].LineIds.Count);
        Assert.Equal("LEE: What do you think happened to Crawford?", Say(lines, crawford[0].LineIds[0]));
        Assert.StartsWith("MOLLY: Don't know, don't care.", Say(lines, crawford[0].LineIds[1]));

        // The third option's text comes from a conditional's cases.
        var conditional = Assert.Single(dialog.Chain(menu.Options.ElementAt(2)));
        Assert.Equal(DlogNodeKind.Conditional, conditional.Kind);
        Assert.Equal(new[] { "Can I borrow your hook?", "Why did you decide to help us?" },
            conditional.Children.Select(c => lines[Assert.Single(c.TextIds)].Text));
        Assert.Same(menu, dialog.Parent(conditional));

        // "Bye" ends the conversation; the post-choice branch loops back to the menu.
        Assert.Equal(new[] { DlogNodeKind.Exchange, DlogNodeKind.Idle, DlogNodeKind.Exit },
            dialog.Chain(menu.Options.Last()).Select(n => n.Kind));
        var post = dialog.Chain(menu.Children.Last()).ToList();
        var jump = post.Last();
        Assert.Equal(DlogNodeKind.Jump, jump.Kind);
        Assert.Equal(DlogJumpTarget.ToParent, jump.Jump!.Target);
        Assert.Same(menu, dialog.JumpTarget(jump));
        Assert.Same(conversation, dialog.ConversationOf(jump));

        Assert.All(dialog.AllTextIds(), id => Assert.True(lines.ContainsKey(id), $"line {id} not in the landb"));
    }

    [SkippableFact]
    public void Reads_named_conversations_and_jumps_from_tales_from_the_borderlands()
    {
        var (dialog, lines) = Read("dlog_tftb_env_01_framestoryprosperityjunction_rhys.dlog",
            "landb_tftb_env_01_framestoryprosperityjunction_rhys_english.landb");

        Assert.Equal(MetaStreamFormat.Msv6, dialog.Format);
        Assert.All(dialog.Nodes, n => Assert.True(n.IsComplete));
        var names = dialog.Conversations.Select(c => dialog.NameOf(c)).ToList();
        Assert.Contains("cs_enter", names);
        Assert.Contains("cs_exit", names);

        var menu = Assert.Single(dialog.Nodes, n => n.Kind == DlogNodeKind.Choices);
        Assert.Equal(new[] { "Who are you?", "What is happening?", "Where are we going?", "" }, OptionTexts(dialog, menu, lines));
        var first = dialog.Head(menu.Options.First())!;
        Assert.Equal("RHYS: So... who are you?", Say(lines, first.LineIds[0]));

        // Jumps by name land on the named start node; others leave for another dialog.
        var exit = dialog.Conversations.Single(c => dialog.NameOf(c) == "cs_exit");
        var toExit = dialog.Nodes.Where(n => n.Kind == DlogNodeKind.Jump && dialog.JumpTarget(n) == dialog.Head(exit)).ToList();
        Assert.NotEmpty(toExit);
        var leaving = dialog.Nodes.First(n => n.Kind == DlogNodeKind.Jump && n.Jump!.OtherDialog != 0);
        Assert.Null(dialog.JumpTarget(leaving));

        Assert.Contains(dialog.Nodes, n => n.Kind == DlogNodeKind.Script && n.Script!.Contains("Money_Reset()"));
        int resolved = dialog.AllTextIds().Count(lines.ContainsKey);
        Assert.Equal(dialog.AllTextIds().Count(), resolved);
    }

    [SkippableFact]
    public void Reads_an_msv5_dialog_from_game_of_thrones()
    {
        var (dialog, lines) = Read("dlog_got_env_oceanwesteros.dlog", "landb_got_env_oceanwesteros_english.landb");

        Assert.Equal(MetaStreamFormat.Msv5, dialog.Format);
        var menu = Assert.Single(dialog.Nodes, n => n.Kind == DlogNodeKind.Choices);
        Assert.Equal(new[] { "They will follow Rodrik.", "The men answer to me.", "I'm just a soldier now.", "" }, OptionTexts(dialog, menu, lines));
        var rodrik = dialog.Chain(menu.Options.First()).Single(n => n.Kind == DlogNodeKind.Exchange);
        Assert.Equal("ASHER: Rodrik will be in command. He's lord of the house. He's been bred for it.", Say(lines, rodrik.LineIds[0]));
        Assert.All(dialog.AllTextIds(), id => Assert.True(lines.ContainsKey(id)));
    }

    [SkippableFact]
    public void Reads_an_msv5_dialog_from_the_wolf_among_us()
    {
        var (dialog, lines) = Read("dlog_wau_env_openarmslobby.dlog", "landb_wau_env_openarmslobby_english.landb");

        Assert.Equal(MetaStreamFormat.Msv5, dialog.Format);
        Assert.All(dialog.Nodes, n => Assert.True(n.IsComplete));
        var start = dialog.Head(dialog.Conversations.First())!;
        Assert.Equal("cs_enter", start.Name);
        var menu = dialog.Chain(dialog.Conversations.First()).First(n => n.Kind == DlogNodeKind.Choices);
        Assert.Equal(new[] { "That piece of shit!", "I'll kill him!", "Fucking creep!", "" }, OptionTexts(dialog, menu, lines));
        Assert.Equal("BIGBY: That motherfucking piece of shit...", Say(lines, dialog.Head(menu.Options.First())!.LineIds[0]));

        // The post-choice branch jumps by name to the next menu.
        var jump = dialog.Chain(menu.Children.Last()).Last();
        Assert.Equal(DlogNodeKind.Jump, jump.Kind);
        Assert.Equal(DlogNodeKind.Choices, dialog.JumpTarget(jump)!.Kind);
        Assert.All(dialog.AllTextIds(), id => Assert.True(lines.ContainsKey(id)));
    }

    [SkippableFact]
    public void Walks_a_choice_hub_like_the_game()
    {
        var (dialog, lines) = Read("dlog_wdc_crawfordautoshop_lee_molly.dlog", "landb_wdc_crawfordautoshop_lee_molly_english.landb");
        var walker = new DlogWalker(dialog);
        walker.Start(dialog.Conversations.Single());

        var step = walker.Next();
        Assert.Equal(DlogStepKind.Line, step.Kind);
        Assert.Equal("MOLLY: Something you need?", Say(lines, step.LineId));

        // The conditional option offers both of its cases.
        var menu = walker.Next();
        Assert.Equal(DlogStepKind.Decision, menu.Kind);
        Assert.Equal(DlogDecisionKind.Choice, menu.Decision);
        Assert.Equal(new[] { "So... Crawford...", "About that walker in the alley...", "Can I borrow your hook?", "Why did you decide to help us?", "Bye" },
            menu.Options.Select(o => lines[o.TextId!.Value].Text));

        walker.Choose(0);
        var said = new List<string>();
        while ((step = walker.Next()).Kind == DlogStepKind.Line)
            said.Add(Say(lines, step.LineId));
        Assert.Equal(4, said.Count);
        Assert.Equal("LEE: What do you think happened to Crawford?", said[0]);

        // The post-choice branch jumps back to the hub.
        Assert.Equal(DlogStepKind.Decision, step.Kind);
        Assert.Equal(5, step.Options.Count);
        walker.Choose(2);
        said.Clear();
        while ((step = walker.Next()).Kind == DlogStepKind.Line)
            said.Add(Say(lines, step.LineId));
        Assert.Equal("LEE: Think I can borrow that hook thing of yours?", said[0]);
        Assert.Equal("MOLLY: I got your promise now. Not a scratch!", said[^1]);
        Assert.Equal(DlogStepKind.End, step.Kind);
        Assert.Equal(DlogEndReason.Exit, step.End);
    }

    [SkippableFact]
    public void Walks_into_another_dialog_and_back_to_named_nodes()
    {
        var (dialog, lines) = Read("dlog_tftb_env_01_framestoryprosperityjunction_rhys.dlog",
            "landb_tftb_env_01_framestoryprosperityjunction_rhys_english.landb");
        var walker = new DlogWalker(dialog);
        walker.Start(dialog.Conversations.Single(c => dialog.NameOf(c) == "cs_exit"));
        var step = walker.Next();
        Assert.Equal(DlogStepKind.End, step.Kind);
        Assert.Equal(DlogEndReason.OtherDialog, step.End);
        Assert.NotEqual(0ul, step.OtherDialog);

        // Starting inside a choice option returns to the menu's post-choice branch, which jumps on.
        var menu = dialog.Nodes.Single(n => n.Kind == DlogNodeKind.Choices);
        walker.StartAt(dialog.Head(menu.Options.First())!);
        var said = new List<string>();
        for (int i = 0; i < 40 && (step = walker.Next()).Kind == DlogStepKind.Line; i++)
            said.Add(Say(lines, step.LineId));
        Assert.Equal("RHYS: So... who are you?", said[0]);
        Assert.True(said.Count > 4, string.Join(" | ", said));
    }

    [SkippableFact]
    public void Survives_corrupted_data_without_throwing()
    {
        var original = File.ReadAllBytes(SampleData.Sample("dlog_wdc_crawfordautoshop_lee_molly.dlog"));
        var header = MetaStreamHeader.Parse(original);
        var random = new Random(7);
        for (int round = 0; round < 300; round++)
        {
            var data = (byte[])original.Clone();
            for (int i = 0; i < 1 + round % 8; i++)
                data[random.Next(header.DefaultSectionOffset, data.Length)] = (byte)random.Next(256);
            var ok = DlogFile.TryRead(data, out var dialog, out var error);
            Assert.True(ok ? dialog is not null : error is not null);
        }
    }

    [SkippableFact]
    public void Rejects_files_that_are_not_dialogs()
    {
        Assert.False(DlogFile.TryRead(File.ReadAllBytes(SampleData.Sample("landb_tftb_death_english.landb")), out _, out var error));
        Assert.NotNull(error);
        Assert.False(DlogFile.TryRead(new byte[] { 1, 2, 3 }, out _, out _));
    }

    [SkippableTheory]
    [InlineData("dlog_wdc_crawfordautoshop_lee_molly.dlog", 314699777u, "crawfordautoshop_lee_molly_use_molly_3.chore")]
    [InlineData("dlog_got_env_oceanwesteros.dlog", 366247950u, "env_whiteHarborGate_cs_seaMontage_13.chore")]
    [InlineData("dlog_tftb_env_01_framestoryprosperityjunction_rhys.dlog", 361570435u, "ui_openingIntro.chore")]
    public void Exchanges_name_the_chore_that_voices_their_lines(string dlog, uint line, string chore)
    {
        Assert.True(DlogFile.TryRead(File.ReadAllBytes(SampleData.Sample(dlog)), out var dialog, out var error), error);
        var exchange = Assert.Single(dialog!.Nodes, n => n.Kind == DlogNodeKind.Exchange && n.LineIds.Contains(line));
        Assert.Equal(TelltaleCrc64.HashSymbol(chore), exchange.ChoreSymbol);
        Assert.All(dialog.Nodes.Where(n => n.Kind == DlogNodeKind.Exchange), n => Assert.NotEqual(0ul, n.ChoreSymbol));
    }

    [SkippableTheory]
    [InlineData("env_heliosOffice.dlog", "env_heliosoffice")]
    [InlineData("crawfordAutoShop_lee_molly.dlog", "crawfordautoshop_lee_molly")]
    public void Scene_names_match_language_databases(string dlog, string scene) =>
        Assert.Equal(scene, DlogFile.SceneOf(dlog));
}
