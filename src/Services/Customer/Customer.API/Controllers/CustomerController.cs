using Microsoft.AspNetCore.Mvc;

namespace Customer.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CustomerController : ControllerBase
{
    private readonly ILogger<CustomerController> _logger;

    public CustomerController(ILogger<CustomerController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetAll()
    {
        // TODO: Implement — migrate logic from monolith's CustomerController
        return Ok(new { service = "Customer", status = "scaffold" });
    }

    [HttpGet("{id}")]
    public IActionResult GetById(int id)
    {
        // TODO: Implement — migrate logic from monolith
        return Ok(new { service = "Customer", id });
    }
}
