using StykkerStarter.Core.Config;

namespace StykkerStarter.Tests;

// Bearbeiten (die Regeln) und die Reihenfolge der Programme
public class EditTests
{
    private static ProgramEntry Entry(string id, string name, string exe, string args = "") =>
        new() { Id = id, Name = name, SourceExe = exe, Arguments = args };

    private static ProgramChanges Changes(string name = "Demo", string args = "", int delay = 0, string ready = "") =>
        new(name, args, StartWithStarter: true, UpdateWithoutAsking: false, KeepPrevious: true, CopyToLocal: true,
            RestartOnCrash: false, StartDelaySeconds: delay, ReadyCheck: ready);

    [Fact]
    public void A_valid_change_has_no_problem_and_applies_the_values()
    {
        var program = Entry("demo", "Demo", @"C:\Programs\demo.exe");
        var config = new StarterConfig { Programs = { program } };
        var changes = Changes(name: "  Demo two ", args: " --x ", delay: 30, ready: " 8080 ") with { RestartOnCrash = true };

        Assert.Null(ProgramEdit.Problem(config, program, changes));
        ProgramEdit.Apply(program, changes);

        Assert.Equal("Demo two", program.Name);
        Assert.Equal("--x", program.Arguments);
        Assert.True(program.RestartOnCrash);
        Assert.Equal(30, program.StartDelaySeconds);
        Assert.Equal("8080", program.ReadyCheck);
    }

    [Fact]
    public void An_empty_name_is_refused()
    {
        var program = Entry("demo", "Demo", @"C:\Programs\demo.exe");
        var config = new StarterConfig { Programs = { program } };

        Assert.NotNull(ProgramEdit.Problem(config, program, Changes(name: "  ")));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    public void The_start_delay_is_between_0_and_120_seconds(int delay)
    {
        var program = Entry("demo", "Demo", @"C:\Programs\demo.exe");
        var config = new StarterConfig { Programs = { program } };

        Assert.NotNull(ProgramEdit.Problem(config, program, Changes(delay: delay)));
        Assert.Null(ProgramEdit.Problem(config, program, Changes(delay: 0)));
        Assert.Null(ProgramEdit.Problem(config, program, Changes(delay: 120)));
    }

    [Fact]
    public void A_ready_check_that_is_not_a_port_or_local_address_is_refused()
    {
        var program = Entry("demo", "Demo", @"C:\Programs\demo.exe");
        var config = new StarterConfig { Programs = { program } };

        Assert.NotNull(ProgramEdit.Problem(config, program, Changes(ready: "http://example.com")));
        Assert.Null(ProgramEdit.Problem(config, program, Changes(ready: "")));
    }

    [Fact]
    public void Editing_into_the_same_file_and_arguments_as_another_entry_is_refused()
    {
        var other = Entry("other", "Other name", @"C:\Programs\demo.exe", "--x");
        var program = Entry("demo", "Demo", @"C:\Programs\demo.exe", "");
        var config = new StarterConfig { Programs = { program, other } };

        Assert.Equal("Other name is already in the list", ProgramEdit.Problem(config, program, Changes(args: "--x")));
        Assert.Null(ProgramEdit.Problem(config, program, Changes(args: "")));
    }

    [Fact]
    public void Move_puts_the_entry_at_the_new_position_and_clamps_the_index()
    {
        var config = new StarterConfig
        {
            Programs = { Entry("a", "A", "a.exe"), Entry("b", "B", "b.exe"), Entry("c", "C", "c.exe") },
        };

        Assert.True(ProgramList.Move(config, "c", 0));
        Assert.Equal(new[] { "c", "a", "b" }, config.Programs.Select(p => p.Id));

        Assert.True(ProgramList.Move(config, "c", 99));
        Assert.Equal(new[] { "a", "b", "c" }, config.Programs.Select(p => p.Id));

        Assert.True(ProgramList.Move(config, "b", -5));
        Assert.Equal(new[] { "b", "a", "c" }, config.Programs.Select(p => p.Id));

        Assert.False(ProgramList.Move(config, "missing", 0));
    }

    [Fact]
    public void Startable_keeps_the_list_order_and_leaves_out_broken_entries()
    {
        var config = new StarterConfig
        {
            Programs = { Entry("a", "A", "a.exe"), Entry("broken", "", "b.exe"), Entry("c", "C", "c.exe") },
        };

        Assert.Equal(new[] { "a", "c" }, ProgramList.Startable(config).Select(p => p.Id));
    }
}
