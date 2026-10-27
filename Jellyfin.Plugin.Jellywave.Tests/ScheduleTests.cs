using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

public class ScheduleTests
{
    private static ScheduleInfo Info(string timezone = "UTC")
    {
        var tuesday = new List<string?>(new string?[24]);
        for (var h = 6; h < 8; h++) { tuesday[h] = "s_early"; }
        for (var h = 8; h < 12; h++) { tuesday[h] = "s_ville"; }
        tuesday[13] = "s_early";           // same show again later: a separate block
        tuesday[14] = "s_missing";          // id without a show: a gap
        return new ScheduleInfo
        {
            Timezone = timezone,
            Personas = [new SchedulePersona { Id = "p1", Name = "Saffron" }, new SchedulePersona { Id = "p2", Name = "Bobby Tran" }],
            Shows = [new ScheduleShow { Id = "s_early", Name = "Early Shift", PersonaId = "p1" }, new ScheduleShow { Id = "s_ville", Name = "The  Ville", PersonaId = "p2" }],
            Schedule = new Dictionary<string, List<string?>> { ["2"] = tuesday, ["3"] = new List<string?>(new string?[24]) }
        };
    }

    [Fact]
    public void Groups_consecutive_hours_into_blocks_and_marks_the_current_one()
    {
        var tuesday1030 = new DateTime(2026, 10, 27, 10, 30, 0, DateTimeKind.Utc);   // 2026-10-27 is a Tuesday
        var r = JellywaveWebController.BuildToday(Info(), tuesday1030);
        Assert.True(r.Available);
        Assert.Equal("Tuesday", r.Day);
        Assert.Equal(10, r.Hour);
        Assert.Collection(
            r.Today,
            b => { Assert.Equal((6, 8, "Early Shift", "Saffron", false), (b.StartHour, b.EndHour, b.Show, b.Host, b.Now)); },
            b => { Assert.Equal((8, 12, "The Ville", "Bobby Tran", true), (b.StartHour, b.EndHour, b.Show, b.Host, b.Now)); },
            b => { Assert.Equal((13, 14, "Early Shift", "Saffron", false), (b.StartHour, b.EndHour, b.Show, b.Host, b.Now)); });
    }

    [Fact]
    public void Uses_the_station_time_zone_for_today()
    {
        // 02:00 UTC Wednesday is still Tuesday evening in New York.
        var wednesday0200 = new DateTime(2026, 10, 28, 2, 0, 0, DateTimeKind.Utc);
        var r = JellywaveWebController.BuildToday(Info("America/New_York"), wednesday0200);
        Assert.Equal("Tuesday", r.Day);
        Assert.Equal(22, r.Hour);
        Assert.Equal(3, r.Today.Count);
        Assert.DoesNotContain(r.Today, b => b.Now);
    }

    [Fact]
    public void A_takeover_unmarks_the_grid_and_names_the_show_on_air()
    {
        var tuesday1030 = new DateTime(2026, 10, 27, 10, 30, 0, DateTimeKind.Utc);
        var same = JellywaveWebController.BuildToday(Info(), tuesday1030, "the ville");
        Assert.True(same.Today[1].Now);
        Assert.Null(same.Takeover);

        var takeover = JellywaveWebController.BuildToday(Info(), tuesday1030, "Pop-up Special");
        Assert.DoesNotContain(takeover.Today, b => b.Now);
        Assert.Equal("Pop-up Special", takeover.Takeover);
    }

    [Fact]
    public void Empty_day_or_unknown_zone_degrade_gracefully()
    {
        var wednesday1200 = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var r = JellywaveWebController.BuildToday(Info(), wednesday1200);
        Assert.True(r.Available);
        Assert.Empty(r.Today);

        var odd = JellywaveWebController.BuildToday(Info("Mars/Olympus"), wednesday1200);
        Assert.Equal("Wednesday", odd.Day);   // fell back to UTC

        var none = JellywaveWebController.BuildToday(new ScheduleInfo(), wednesday1200);
        Assert.False(none.Available);
    }
}
