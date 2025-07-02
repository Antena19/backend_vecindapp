using Microsoft.AspNetCore.Mvc;
using Transbank.Webpay.Common;
using Transbank.Webpay.WebpayPlus;
using Transbank.Webpay.WebpayPlus.Responses;
using Microsoft.Extensions.Configuration;
using Transbank.Common;
using REST_VECINDAPP.Servicios;
using Microsoft.AspNetCore.Authorization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace REST_VECINDAPP.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WebpayController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<WebpayController> _logger;
        private readonly WebpayService _webpayService;
        private readonly TransbankService _transbankService;

        public WebpayController(IConfiguration configuration, ILogger<WebpayController> logger, WebpayService webpayService, TransbankService transbankService)
        {
            _configuration = configuration;
            _logger = logger;
            _webpayService = webpayService;
            _transbankService = transbankService;
        }

        [HttpPost("create")]
        public async Task<IActionResult> CreateTransaction([FromBody] CreateTransactionRequest request)
        {
            try
            {
                var response = await _webpayService.IniciarTransaccion(request.BuyOrder, request.Amount);
                return Ok(new
                {
                    token = response.Token,
                    url = response.Url
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al crear la transacción de WebPay");
                return StatusCode(500, new { error = "Error al procesar la transacción" });
            }
        }

        [HttpPost("commit")]
        public async Task<IActionResult> CommitTransaction([FromBody] CommitTransactionRequest request)
        {
            try
            {
                var options = new Options(
                    _configuration["Transbank:CommerceCode"],
                    _configuration["Transbank:ApiKey"],
                    WebpayIntegrationType.Test
                );
                var transaction = new Transaction(options);
                var result = transaction.Commit(request.Token);
                // Guardar resultado en base de datos
                await _transbankService.GuardarResultadoPago(request.Token, result.Status, Convert.ToDecimal(result.Amount ?? 0), result.BuyOrder);
                await _transbankService.GuardarPagoEnHistorial(request.Token, result.Status, Convert.ToDecimal(result.Amount ?? 0), result.BuyOrder);

                // NUEVO: Si el pago fue exitoso, confirma el pago y genera el certificado igual que en el endpoint de certificados
                if (result.Status.ToLower() == "authorized")
                {
                    var httpClient = new HttpClient();
                    var baseUrl = _configuration["BaseUrl"] ?? "http://localhost:5000";
                    var confirmUrl = $"{baseUrl}/api/Certificados/pago/confirmar";
                    var payload = new { Token = request.Token };
                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                    // Reintento simple: intenta hasta 3 veces con un pequeño delay
                    for (int i = 0; i < 3; i++)
                    {
                        var response = await httpClient.PostAsync(confirmUrl, content);
                        if (response.IsSuccessStatusCode)
                            break;
                        await Task.Delay(1000); // Espera 1 segundo antes de reintentar
                    }
                }

                if (result.Status.ToLower() != "authorized")
                {
                    var htmlRechazo = $@"
                    <html>
                        <body style='font-family: sans-serif; text-align: center; padding-top: 50px;'>
                            <h2>⚠️ Transacción rechazada</h2>
                            <p><strong>Orden:</strong> {result.BuyOrder}</p>
                            <p><strong>Monto:</strong> ${result.Amount}</p>
                            <p><strong>Estado:</strong> {result.Status}</p>
                            <br>
                            <!--<p><a href='http://localhost:8100/payment/error'>Volver al sitio</a></p>-->
                            <p style='color: #b71c1c; font-weight: bold;'>El pago no fue exitoso.</p>
                            <p>Por favor, vuelve a la aplicación para intentarlo nuevamente o revisar el estado de tu certificado.</p>
                        </body>
                    </html>";
                    return Content(htmlRechazo, "text/html");
                }

                var htmlExito = $@"
                <html>
                    <head>
                        <!--<meta http-equiv='refresh' content='5;url=http://localhost:8100/payment/final' />-->
                    </head>
                    <body style='font-family: sans-serif; text-align: center; padding-top: 50px;'>
                        <h2>✅ ¡Pago confirmado!</h2>
                        <p>Gracias por tu compra.</p>
                        <p><strong>Orden:</strong> {result.BuyOrder}</p>
                        <p><strong>Monto:</strong> ${result.Amount}</p>
                        <p><strong>Estado:</strong> {result.Status}</p>
                        <br>
                        <!--<p>Serás redirigido automáticamente. Si no, haz clic <a href='http://localhost:8100/payment/final'>aquí</a>.</p>-->
                        <p style='color: #388e3c; font-weight: bold;'>Tu pago fue procesado correctamente.</p>
                        <p>Ahora puedes volver a la aplicación para descargar tu certificado de residencia.</p>
                        <p style='font-size: 0.9em; color: #888;'>Puedes cerrar esta ventana y regresar a la app.</p>
                    </body>
                </html>";
                return Content(htmlExito, "text/html");
            }
            catch (Exception ex)
            {
                var htmlError = $@"
                <html>
                    <body style='font-family: sans-serif; text-align: center; padding-top: 50px;'>
                        <h2>❌ Error al procesar el pago</h2>
                        <p>Ocurrió un problema al confirmar el pago.</p>
                        <p><strong>Mensaje:</strong> {ex.Message}</p>
                        <p><a href='http://localhost:8100/payment/error'>Volver al sitio</a></p>
                    </body>
                </html>";
                return Content(htmlError, "text/html");
            }
        }

        [HttpPost("status")]
        public async Task<IActionResult> GetTransactionStatus([FromBody] StatusRequest request)
        {
            try
            {
                var response = await _transbankService.GetTransactionStatus(request.Token);
                return Ok(new
                {
                    amount = response.Amount,
                    status = response.Status,
                    buyOrder = response.BuyOrder,
                    sessionId = response.SessionId,
                    cardNumber = response.CardDetail?.CardNumber,
                    accountingDate = response.AccountingDate,
                    transactionDate = response.TransactionDate,
                    authorizationCode = response.AuthorizationCode,
                    paymentTypeCode = response.PaymentTypeCode,
                    responseCode = response.ResponseCode,
                    installmentsNumber = response.InstallmentsNumber
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al obtener el estado de la transacción de WebPay");
                return StatusCode(500, new { error = "Error al obtener el estado de la transacción" });
            }
        }

        [HttpGet("test-init")]
        public async Task<IActionResult> TestInitEndpoint()
        {
            try
            {
                var result = await _transbankService.TestInitEndpoint();
                return Ok(new { 
                    success = result, 
                    message = result ? "Endpoint /init funciona correctamente" : "Endpoint /init no responde correctamente",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al probar el endpoint /init");
                return StatusCode(500, new { 
                    error = "Error al probar el endpoint /init", 
                    message = ex.Message,
                    timestamp = DateTime.Now
                });
            }
        }

        [HttpGet("iniciar-sdk/{solicitudId}/{monto}")]
        [Authorize]
        public async Task<IActionResult> IniciarTransaccionSDK(int solicitudId, decimal monto)
        {
            var resultado = await _transbankService.IniciarTransaccionSDKAsync(solicitudId, monto);

            if (resultado.Exito)
            {
                return Ok(new
                {
                    success = true,
                    token = resultado.Token,
                    url = resultado.Url
                });
            }

            return BadRequest(new
            {
                success = false,
                message = resultado.Mensaje
            });
        }
    }

    public class StatusRequest
    {
        public string Token { get; set; } = string.Empty;
    }
} 