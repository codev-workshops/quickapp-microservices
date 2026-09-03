using Microsoft.AspNetCore.Mvc;

namespace Identity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class IdentityController : ControllerBase
{
    private readonly ILogger<IdentityController> _logger;

    public IdentityController(ILogger<IdentityController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        // TODO: Implement — migrate logic from monolith's IdentityController
        return Ok(new { service = "Identity", status = "scaffold" });
    }

    [HttpGet("{id}")]
    public IActionResult GetById(int id)
    {
        // TODO: Implement — migrate logic from monolith
        return Ok(new { service = "Identity", id });
    }
}
