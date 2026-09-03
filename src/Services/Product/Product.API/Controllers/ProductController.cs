using Microsoft.AspNetCore.Mvc;

namespace Product.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductController : ControllerBase
{
    private readonly ILogger<ProductController> _logger;

    public ProductController(ILogger<ProductController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        // TODO: Implement — migrate logic from monolith's ProductController
        return Ok(new { service = "Product", status = "scaffold" });
    }

    [HttpGet("{id}")]
    public IActionResult GetById(int id)
    {
        // TODO: Implement — migrate logic from monolith
        return Ok(new { service = "Product", id });
    }
}
