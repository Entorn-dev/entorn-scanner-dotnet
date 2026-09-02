using Microsoft.AspNetCore.Mvc;

namespace Reference.Inventory;

[ApiController]
[Route("api/[controller]")]
public sealed class InventoryController : ControllerBase
{
    [HttpGet("{id}")]
    public IActionResult Get(string id) => Ok(id);

    [HttpPost]
    [Route("reserve")]
    public IActionResult Reserve() => Accepted();
}
