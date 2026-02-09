using IdempotencyKey.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace MvcControllerSample.Controllers;

[ApiController]
[Route("[controller]")]
public class PaymentsController : ControllerBase
{
    private static int _counter = 0;

    [HttpPost]
    [RequireIdempotency(TtlSeconds = 60)]
    public IActionResult Create([FromBody] PaymentRequest request)
    {
        Interlocked.Increment(ref _counter);
        return Ok(new { Status = "Processed", TransactionId = Guid.NewGuid().ToString(), Request = request });
    }

    [HttpGet("count")]
    public int GetCount() => _counter;
}

public record PaymentRequest(decimal Amount, string Currency);
