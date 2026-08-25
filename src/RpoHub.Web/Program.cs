using RpoHub.Application;
using RpoHub.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRpoHubInfrastructure(builder.Configuration);
var app = builder.Build();

app.MapGet("/", () => Results.Content("""
<!doctype html><html lang="sk"><meta charset="utf-8"><title>RpoHub</title>
<style>body{font:16px system-ui;max-width:880px;margin:4rem auto;padding:0 1rem;color:#172033}code{background:#eef2f7;padding:.15rem .35rem;border-radius:.25rem}</style>
<h1>RpoHub</h1><p>Foundation is running.</p>
<ul><li><code>GET /api/status</code> — initialization safety state</li><li><code>GET /api/import/initialization</code> — preview official initialization packages</li><li><code>POST /api/import/initialization/{snapshotDate}/start</code> — validate and prepare initialization</li><li><code>GET /api/rpo/search/{ico}</code> — live RPO verification</li><li><code>POST /api/import/discover</code> — discover official daily files</li></ul></html>
""", "text/html; charset=utf-8"));

app.MapGet("/api/status", (IImportStateStore store, CancellationToken ct) => store.GetReadinessAsync(ct));
app.MapGet("/api/import/initialization", (GetRpoInitializationPreview useCase, CancellationToken ct) => useCase.ExecuteAsync(ct));
app.MapPost("/api/import/initialization/{snapshotDate}/start", async (DateOnly snapshotDate, StartRpoInitialization useCase, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await useCase.ExecuteAsync(snapshotDate, ct));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
});
app.MapGet("/api/rpo/search/{ico}", (string ico, IRpoApiClient client, CancellationToken ct) => client.SearchByIcoAsync(ico, ct));
app.MapPost("/api/import/discover", async (DiscoverRpoUpdates useCase, CancellationToken ct) => Results.Ok(new { discovered = await useCase.ExecuteAsync(ct) }));
app.Run();
