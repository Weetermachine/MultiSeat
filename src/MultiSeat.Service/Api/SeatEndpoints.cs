using Microsoft.AspNetCore.Mvc;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Api;

public static class SeatEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/seats").WithTags("Seats");

        group.MapGet("/", (SeatManager mgr) =>
            Results.Ok(mgr.GetAllSeats()));

        group.MapGet("/{id:guid}", (Guid id, SeatManager mgr) =>
        {
            var seat = mgr.GetSeat(id);
            return seat is null ? Results.NotFound() : Results.Ok(seat);
        });

        group.MapPost("/", async (SeatRequest request, SeatManager mgr, CancellationToken ct) =>
        {
            if (!ApiInputValidation.IsValidAccountName(request.AccountName))
                return ApiInputValidation.AccountNameError();
            try
            {
                var seat = await mgr.ProvisionSeatAsync(request, ct);
                return Results.Created($"/api/seats/{seat.Id}", seat);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{id:guid}/launch",
            async (Guid id, LaunchAppRequest request, SeatManager mgr, CancellationToken ct) =>
            {
                var seat = mgr.GetSeat(id);
                if (seat is null)
                    return Results.NotFound();

                try
                {
                    await mgr.LaunchAppInSeatAsync(id, request, ct);
                    return Results.Ok(new { status = "launched" });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        group.MapDelete("/{id:guid}", async (Guid id, SeatManager mgr, CancellationToken ct) =>
        {
            var seat = mgr.GetSeat(id);
            if (seat is null)
                return Results.NotFound();

            await mgr.TeardownSeatAsync(id, ct);
            return Results.NoContent();
        });

        // ── Per-seat service management ────────────────────────────────

        group.MapGet("/{id:guid}/services", (Guid id, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null)
                return Results.NotFound();
            return Results.Ok(mgr.GetSeatServices(id));
        });

        group.MapPost("/{id:guid}/apollo/stop", (Guid id, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null)
                return Results.NotFound();
            try
            {
                mgr.StopApollo(id);
                return Results.Ok(new { status = "stopped" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{id:guid}/apollo/start",
            async (Guid id, SeatManager mgr, CancellationToken ct) =>
            {
                if (mgr.GetSeat(id) is null)
                    return Results.NotFound();
                try
                {
                    await mgr.StartApolloAsync(id, ct);
                    return Results.Ok(new { status = "started" });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        group.MapPost("/{id:guid}/apollo/restart",
            async (Guid id, SeatManager mgr, CancellationToken ct) =>
            {
                if (mgr.GetSeat(id) is null)
                    return Results.NotFound();
                try
                {
                    await mgr.RestartApolloAsync(id, ct);
                    return Results.Ok(new { status = "restarted" });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        // No /audio/reset — audio is per-session (audiomode:i:0) with no device assignment
        // and no session default to re-apply, so there is nothing a reset could do.

        group.MapPost("/{id:guid}/display/reset",
            async (Guid id, SeatManager mgr, CancellationToken ct) =>
            {
                if (mgr.GetSeat(id) is null)
                    return Results.NotFound();
                try
                {
                    await mgr.ResetDisplayAsync(id, ct);
                    return Results.Ok(new { status = "reset" });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        group.MapPost("/{id:guid}/controller/reset", (Guid id, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null)
                return Results.NotFound();
            try
            {
                mgr.ResetController(id);
                return Results.Ok(new { status = "reset" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ── Presets ────────────────────────────────────────────────────

        group.MapGet("/presets", (SeatPresetStore presets) =>
            Results.Ok(presets.GetAll()));

        group.MapPut("/{id:guid}/autostart",
            (Guid id, AutoStartRequest req, SeatManager mgr, SeatPresetStore presets) =>
            {
                var seat = mgr.GetSeat(id);
                if (seat is null) return Results.NotFound();

                seat.AutoStart = req.Enabled;

                if (req.Enabled)
                {
                    presets.Upsert(new SeatPreset
                    {
                        AccountName = seat.AccountName,
                        Width = seat.Width,
                        Height = seat.Height,
                        Fps = seat.Fps,
                        AutoStart = true,
                        NvencPreset = seat.NvencPreset,
                    });
                }
                else
                {
                    presets.DeleteByAccount(seat.AccountName);
                }

                return Results.Ok(new { autoStart = seat.AutoStart });
            });

        group.MapPost("/{id:guid}/session-reconnect",
            async (Guid id, SeatManager mgr, SessionLauncher sessionLauncher, CancellationToken ct) =>
            {
                var seat = mgr.GetSeat(id);
                if (seat is null)
                    return Results.NotFound();

                // Pass the seat's geometry so the reconnected session keeps its configured size
                // instead of silently reverting to the console session's.
                await sessionLauncher.LaunchSessionAsync(seat.AccountName, ct, seat.Width, seat.Height);
                return Results.Ok(new { sessionId = seat.SessionId, message = "Session reconnected" });
            });

        // ── Paired client management ───────────────────────────────────

        group.MapGet("/{id:guid}/clients", (Guid id, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null) return Results.NotFound();
            return Results.Ok(mgr.GetPairedClients(id));
        });

        group.MapDelete("/{id:guid}/clients", (Guid id, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null) return Results.NotFound();
            mgr.UnpairAllClients(id);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}/clients/{name}", (Guid id, string name, SeatManager mgr) =>
        {
            if (mgr.GetSeat(id) is null) return Results.NotFound();
            var removed = mgr.UnpairClient(id, name);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        group.MapPost("/{id:guid}/nvenc-preset",
            async (Guid id, NvencPresetRequest req, SeatManager mgr,
                   SeatPresetStore presets, CancellationToken ct) =>
            {
                if (mgr.GetSeat(id) is null)
                    return Results.NotFound();
                try
                {
                    await mgr.SetNvencPresetAsync(id, req.Preset, presets, ct);
                    return Results.Ok(new { preset = req.Preset.ToString() });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });
    }
}
