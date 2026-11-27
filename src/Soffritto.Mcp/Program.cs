using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Soffritto.Mcp;

// Worker mode: one operation started by a server, detached from it (see Operations).
if (args is [OperationWorker.Argument, var operation])
{
    return await OperationWorker.RunAsync(operation);
}

var builder = Host.CreateApplicationBuilder(args);

// stdout is the JSON-RPC channel: any log written there breaks the protocol.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddMcpServer(o => o.ServerInstructions =
        "Numbered development environments, from the same configuration as the desktop app. An environment N "
        + "groups repositories from the catalog (backend, frontend, app, generic). Two kinds. "
        + "LOCAL: clones on this machine, where people also work by hand; the tools do what the app's buttons do. "
        + "Services (Kestrel, dev server, app, bundler) start and stop with start_local_service / stop_local_service "
        + "and start_local_environment / stop_local_environment, in consoles detached from the server. Git: pull_repository "
        + "(fast-forward only), switch_branch, checkout_local_environment (every repository onto a branch, like Reset and "
        + "\"Open in\": it checks them all first and changes nothing if one is not clean). Irreversible actions go in two steps: "
        + "prune_branches, apply_local_migrations and update_local_database_to first return a plan and a confirmation code; "
        + "show the plan to the person and call again with confirm = code only if they agree. A local environment with "
        + "protectedDatabase=true uses a protected database (for example one shared with others): no migrations are applied to "
        + "it and its schema is never altered, so do not pick it for work that creates or applies migrations. "
        + "list_environments and environment_status read the state. "
        + "DOCKER, disposable and dedicated to one piece of work: each repository is a worktree on a branch, and a "
        + "compose project runs the database (restored from a seed and migrated to the branch), the api and the web; "
        + "act on these freely with the *_docker tools. "
        + "Typical flow for new work: list_environments for the catalog ids; create_docker_environment "
        + "with the branch of each repository (a branch that does not exist is created from the main development "
        + "branch) and a note saying what the environment is for; write code in the worktrees named in the "
        + "response; the web receives saved code through Compose Watch, the api is rebuilt with "
        + "start_docker_service; after a new migration, apply_docker_migrations; docker_environment_status "
        + "and docker_service_logs to verify. Commit and push with git in the worktrees. "
        + "For every environment: run_extra_command runs a repository's extra command (extraCommand in list_environments), "
        + "enable_tunnel / disable_tunnel turn the environment's tunnel on and off, set_environment_note writes its note. "
        + "environments_work, and the 'work' section of environment_status and docker_environment_status, say which work items "
        + "(from the user's active PRs on Azure DevOps) an environment sits on: like the environment tag of the app's PR panel. "
        + "Long operations (create, start, migrations, git, extra command) run in a worker process of their own: the response "
        + "contains an id to follow with operation_status, and the operation goes on even if this server or the session ends. "
        + "One operation at a time runs on an environment. A Docker environment can only be destroyed from the app: a "
        + "person does it with \"Destroy environment\" on the environment's column, so whoever asks to destroy one "
        + "should be pointed to that button. When the work is done, leave the environment running unless the "
        + "person using it says otherwise.")
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

if (Soffritto.Core.Configuration.ConfigurationFiles.DataFolderProblem is null)
{
    Operations.Prune();
}

await builder.Build().RunAsync();
return 0;
