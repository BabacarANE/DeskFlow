using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Desk.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Desk.IntegrationTests;

/// <summary>Company palette (US-19): roles, validation, upper-casing, contrast warning, reset, /me, isolation.</summary>
public class ThemeTests(PostgresFixture postgres) : TicketTestBase(postgres)
{
    private static readonly ContractSchema Contract = ContractSchema.Load().AsStrict();

    private static readonly object Valid = new { colorPrimary = "#112233", colorSurface = "#ffffff", colorAccent = "#aa0000" };

    private static void AssertConforms(JsonElement body, string method, HttpStatusCode status)
    {
        var errors = Contract.Validate(body, Contract.ResponseSchema("/theme", method, (int)status));
        Assert.True(errors.Count == 0, $"{method} /theme {(int)status}: {string.Join("; ", errors)}\n{body.GetRawText()}");
    }

    private static string[] Colors(JsonElement theme) =>
        [theme.GetProperty("colorPrimary").GetString()!, theme.GetProperty("colorSurface").GetString()!, theme.GetProperty("colorAccent").GetString()!];

    private static readonly string[] Defaults = ["#7CC4EE", "#F4F7FA", "#D63A14"];

    private static async Task<string[]> CurrentAsync(Session session)
    {
        var response = await session.GetAsync("/api/v1/theme");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        AssertConforms(json, "get", HttpStatusCode.OK);
        return Colors(json);
    }

    [Fact]
    public async Task Everybody_reads_the_theme_only_the_tenant_admin_writes_and_the_superadmin_gets_the_defaults()
    {
        var c = await BuildCompanyAsync("theme-roles");
        var root = await SignInSuperAdminAsync();

        foreach (var reader in new[] { c.Admin.Session, c.Agent.Session, c.Requester.Session, root })
        {
            Assert.Equal(Defaults, await CurrentAsync(reader));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/v1/theme")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.PutAsJsonAsync("/api/v1/theme", Valid)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.DeleteAsync("/api/v1/theme")).StatusCode);

        foreach (var other in new[] { c.Agent.Session, c.Requester.Session, root })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsync("/api/v1/theme", Valid)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync("/api/v1/theme")).StatusCode);
        }

        // Nothing moved, in the database nor for the SuperAdmin.
        Assert.Equal(Defaults, await CurrentAsync(c.Admin.Session));
        Assert.Equal(Defaults, await CurrentAsync(root));
        Assert.Equal(HttpStatusCode.OK, (await c.Admin.Session.PutAsync("/api/v1/theme", Valid)).StatusCode);
        // The SuperAdmin still reads the platform defaults, not a company palette.
        Assert.Equal(Defaults, await CurrentAsync(root));
    }

    [Fact]
    public async Task Updating_validates_each_color_stores_upper_case_warns_on_low_contrast_and_feeds_me()
    {
        var c = await BuildCompanyAsync("theme-update");
        var admin = c.Admin.Session;

        // 422 with one error per bad field; nothing is stored.
        var bad = await admin.PutAsync("/api/v1/theme", new { colorPrimary = "red", colorSurface = "#12345", colorAccent = "#GGGGGG" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var errors = (await JsonAsync(bad)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("colorPrimary", out _));
        Assert.True(errors.TryGetProperty("colorSurface", out _));
        Assert.True(errors.TryGetProperty("colorAccent", out _));

        foreach (var (body, field) in new (object, string)[]
        {
            (new { colorPrimary = "#12345678", colorSurface = "#FFFFFF", colorAccent = "#000000" }, "colorPrimary"),
            (new { colorPrimary = "#112233", colorSurface = " #FFFFFF", colorAccent = "#000000" }, "colorSurface"),
            (new { colorPrimary = "#112233", colorSurface = "#FFFFFF", colorAccent = "000000" }, "colorAccent"),
            (new { colorPrimary = "#112233", colorSurface = "#FFFFFF" }, "colorAccent"),
            (new { colorPrimary = (string?)null, colorSurface = "#FFFFFF", colorAccent = "#000000" }, "colorPrimary"),
        })
        {
            var response = await admin.PutAsync("/api/v1/theme", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), field);
        }

        Assert.Equal(Defaults, await CurrentAsync(admin));

        // Lower case input is stored upper case; a readable accent gives no warning.
        var ok = await admin.PutAsync("/api/v1/theme", Valid);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var result = await JsonAsync(ok);
        AssertConforms(result, "put", HttpStatusCode.OK);
        Assert.Equal(["#112233", "#FFFFFF", "#AA0000"], Colors(result.GetProperty("theme")));
        Assert.Empty(result.GetProperty("warnings").EnumerateArray());
        Assert.Equal("#AA0000", await QueryDbAsync(db => db.Tenants.Where(t => t.Id == c.TenantId).Select(t => t.ColorAccent).SingleAsync()));

        // Every role sees it, and /me carries the same object.
        foreach (var reader in new[] { c.Agent.Session, c.Requester.Session, admin })
        {
            Assert.Equal(["#112233", "#FFFFFF", "#AA0000"], await CurrentAsync(reader));
            var me = await JsonAsync(await reader.GetAsync("/api/v1/me"));
            Assert.Equal(["#112233", "#FFFFFF", "#AA0000"], Colors(me.GetProperty("tenant").GetProperty("theme")));
        }

        // A weak accent on the surface is saved with a French, non blocking warning.
        var weak = await admin.PutAsync("/api/v1/theme", new { colorPrimary = "#112233", colorSurface = "#FFFFFF", colorAccent = "#eeeeee" });
        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);
        var weakResult = await JsonAsync(weak);
        AssertConforms(weakResult, "put", HttpStatusCode.OK);
        var warning = Assert.Single(weakResult.GetProperty("warnings").EnumerateArray()).GetString()!;
        Assert.Contains("contraste", warning);
        Assert.Contains("3:1", warning);
        Assert.Equal("#EEEEEE", (await CurrentAsync(admin))[2]);

        // Just on the right side of the limit: no warning (#767676 on white is 4.54:1; #949494 is 3.03:1).
        foreach (var accent in new[] { "#767676", "#949494" })
        {
            var fine = await JsonAsync(await admin.PutAsync("/api/v1/theme", new { colorPrimary = "#112233", colorSurface = "#FFFFFF", colorAccent = accent }));
            Assert.Empty(fine.GetProperty("warnings").EnumerateArray());
        }

        // Just under 3:1 (#959595 on white is 2.99:1): warning.
        var under = await JsonAsync(await admin.PutAsync("/api/v1/theme", new { colorPrimary = "#112233", colorSurface = "#FFFFFF", colorAccent = "#959595" }));
        Assert.Single(under.GetProperty("warnings").EnumerateArray());

        // Reset gives the defaults back, also in /me.
        var reset = await admin.DeleteAsync("/api/v1/theme");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var resetJson = await JsonAsync(reset);
        AssertConforms(resetJson, "delete", HttpStatusCode.OK);
        Assert.Equal(Defaults, Colors(resetJson));
        var meAfter = await JsonAsync(await c.Requester.Session.GetAsync("/api/v1/me"));
        Assert.Equal(Defaults, Colors(meAfter.GetProperty("tenant").GetProperty("theme")));
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/v1/theme")).StatusCode);
    }

    [Fact]
    public async Task A_company_theme_is_invisible_to_and_untouched_by_another_company()
    {
        var alpha = await BuildCompanyAsync("theme-alpha");
        var (betaId, betaAdmin) = await CreateTenantWithAdminAsync("theme-beta", "admin@theme-beta.test");
        Assert.Equal(HttpStatusCode.OK, (await alpha.Admin.Session.PutAsync("/api/v1/theme", Valid)).StatusCode);

        Assert.Equal(Defaults, await CurrentAsync(betaAdmin));
        Assert.Equal(Defaults, Colors((await JsonAsync(await betaAdmin.GetAsync("/api/v1/me"))).GetProperty("tenant").GetProperty("theme")));

        // Beta changes and resets its own palette: alpha keeps its colors.
        Assert.Equal(HttpStatusCode.OK, (await betaAdmin.PutAsync("/api/v1/theme", new { colorPrimary = "#010101", colorSurface = "#FEFEFE", colorAccent = "#101010" })).StatusCode);
        Assert.Equal(["#112233", "#FFFFFF", "#AA0000"], await CurrentAsync(alpha.Requester.Session));
        Assert.Equal(HttpStatusCode.OK, (await betaAdmin.DeleteAsync("/api/v1/theme")).StatusCode);
        Assert.Equal(["#112233", "#FFFFFF", "#AA0000"], await CurrentAsync(alpha.Admin.Session));
        Assert.Equal("#AA0000", await QueryDbAsync(db => db.Tenants.Where(t => t.Id == alpha.TenantId).Select(t => t.ColorAccent).SingleAsync()));
        Assert.Equal("#D63A14", await QueryDbAsync(db => db.Tenants.Where(t => t.Id == betaId).Select(t => t.ColorAccent).SingleAsync()));
    }
}
