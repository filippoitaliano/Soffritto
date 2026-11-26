using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout is the JSON-RPC channel: any log written there breaks the protocol.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddMcpServer(o => o.ServerInstructions =
        "Numbered development environments, from the same configuration as the desktop app. An environment N "
        + "groups repositories from the catalog (backend, frontend, app, generic). Two kinds: "
        + "LOCAL, clones on this machine with services started in console windows; for these the tools are "
        + "read-only (list_environments, environment_status), with two exceptions that work on both local and Docker "
        + "environments: run_extra_command runs the repository's extra command in the clone (extraCommand in "
        + "list_environments), the same as the app's accessory button, and enable_tunnel / disable_tunnel turn the "
        + "environment's tunnel on and off, the same as the \"Tunnel\" switch in its header. A local environment with protectedDatabase=true uses a "
        + "protected database (for example one shared with others): no migrations are applied to it and its schema is never altered, so "
        + "do not pick it for work that creates or applies migrations. "
        + "DOCKER, disposable and dedicated to one piece of work: each repository is a worktree on a branch, and a "
        + "compose project runs the database (restored from a seed and migrated to the branch), the api and the web; "
        + "act on these freely with the *_docker tools. "
        + "Typical flow for new work: list_environments for the catalog ids; create_docker_environment "
        + "with the branch of each repository (a branch that does not exist is created from the main development "
        + "branch) and a note saying what the environment is for; write code in the worktrees named in the "
        + "response; the web receives saved code through Compose Watch, the api is rebuilt with "
        + "start_docker_service; after a new migration, apply_docker_migrations; docker_environment_status "
        + "and docker_service_logs to verify. Commit and push with git in the worktrees. "
        + "Long operations (create, start, migrations) run in the background: the response contains "
        + "an id to follow with operation_status. A Docker environment can only be destroyed from the app: a "
        + "person does it with \"Destroy environment\" on the environment's column, so whoever asks to destroy one "
        + "should be pointed to that button. When the work is done, leave the environment running unless the "
        + "person using it says otherwise.")
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
