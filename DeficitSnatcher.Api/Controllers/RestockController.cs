using DeficitSnatcher.Api.Ai;
using DeficitSnatcher.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeficitSnatcher.Api.Controllers;

/// <summary>Restock forecasts: from each page's own history, and what the AI finds online about the store.</summary>
[ApiController]
[Route("api/restock")]
public sealed class RestockController(AppDbContext db, RestockForecaster forecaster) : ControllerBase
{
    [HttpGet("site/{id:int}")]
    public async Task<ActionResult<RestockForecast>> Site(int id, CancellationToken ct) =>
        await forecaster.ForecastAsync(id, ct) is { } f ? f : NotFound();

    /// <summary>Every page of a product (0: all products), for the lists.</summary>
    [HttpGet("product/{id:int}")]
    public Task<Dictionary<int, RestockForecast>> Product(int id, CancellationToken ct) => forecaster.ForecastProductAsync(id, ct);

    /// <summary>Looks the store's restock pattern up online now (one AI web search), then the page's forecast.</summary>
    [HttpPost("site/{id:int}/refresh")]
    public async Task<ActionResult<RestockForecast>> Refresh(int id, CancellationToken ct)
    {
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (site == null)
            return NotFound();
        if (await forecaster.RefreshOnlineAsync(site.ProductId, site.Url, ct) is { } error)
            return Problem(error, statusCode: 400);
        return (await forecaster.ForecastAsync(id, ct))!;
    }
}
