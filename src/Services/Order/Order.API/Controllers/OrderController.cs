using Microsoft.AspNetCore.Mvc;

namespace Order.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrderController : ControllerBase
{
    private readonly ILogger<OrderController> _logger;

    public OrderController(ILogger<OrderController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        // TODO: Implement — migrate logic from monolith's OrderController
        return Ok(new { service = "Order", status = "scaffold" });
    }

    [HttpGet("{id}")]
    public IActionResult GetById(int id)
    {
        // TODO: Implement — migrate logic from monolith
        return Ok(new { service = "Order", id });
    }
}
