using Aegis.Collectors;

namespace Aegis.Tests.Collectors;

public class StateMappingTests
{
    [Theory]
    [InlineData(0, RunStatus.Failed)]
    [InlineData(1, RunStatus.Succeeded)]
    [InlineData(2, RunStatus.Retry)]
    [InlineData(3, RunStatus.Cancelled)]
    [InlineData(4, RunStatus.Running)]
    [InlineData(99, RunStatus.Unknown)]
    public void Sql_agent_run_status_codes_map_to_the_shared_vocabulary(int code, string expected)
    {
        Assert.Equal(expected, SqlAgentCollector.MapRunStatus(code));
    }

    [Theory]
    [InlineData("success", RunStatus.Succeeded)]
    [InlineData("failed", RunStatus.Failed)]
    [InlineData("running", RunStatus.Running)]
    [InlineData("queued", RunStatus.Queued)]
    [InlineData("restarting", RunStatus.Unknown)]
    [InlineData(null, RunStatus.Unknown)]
    public void Airflow_dag_run_states_map_to_the_shared_vocabulary(string? state, string expected)
    {
        Assert.Equal(expected, AirflowCollector.MapState(state));
    }

    [Theory]
    [InlineData(RunStatus.Succeeded, true)]
    [InlineData(RunStatus.Failed, true)]
    [InlineData(RunStatus.Cancelled, true)]
    [InlineData(RunStatus.Running, false)]
    [InlineData(RunStatus.Queued, false)]
    [InlineData(RunStatus.Retry, false)]
    [InlineData(RunStatus.Unknown, false)]
    public void Only_finished_outcomes_may_close_an_open_run(string status, bool terminal)
    {
        Assert.Equal(terminal, RunStatus.IsTerminal(status));
    }
}
