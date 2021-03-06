using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using ActiveLogin.Authentication.BankId.Api;
using ActiveLogin.Authentication.BankId.Api.Models;
using ActiveLogin.Authentication.BankId.Api.UserMessage;
using ActiveLogin.Authentication.BankId.AspNetCore.Areas.BankIdAuthentication.Models;
using ActiveLogin.Authentication.BankId.AspNetCore.DataProtection;
using ActiveLogin.Authentication.BankId.AspNetCore.Events;
using ActiveLogin.Authentication.BankId.AspNetCore.EndUserContext;
using ActiveLogin.Authentication.BankId.AspNetCore.Events.Infrastructure;
using ActiveLogin.Authentication.BankId.AspNetCore.Launcher;
using ActiveLogin.Authentication.BankId.AspNetCore.Models;
using ActiveLogin.Authentication.BankId.AspNetCore.Qr;
using ActiveLogin.Authentication.BankId.AspNetCore.SupportedDevice;
using ActiveLogin.Authentication.BankId.AspNetCore.UserMessage;
using ActiveLogin.Identity.Swedish;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ActiveLogin.Authentication.BankId.AspNetCore.Areas.BankIdAuthentication.Controllers
{
    [Area(BankIdConstants.AreaName)]
    [Route("/[area]/Api/")]
    [ApiController]
    [AllowAnonymous]
    public class BankIdApiController : Controller
    {
        private const string UserAgentHttpHeaderName = "User-Agent";

        private readonly UrlEncoder _urlEncoder;
        private readonly IBankIdUserMessage _bankIdUserMessage;
        private readonly IBankIdUserMessageLocalizer _bankIdUserMessageLocalizer;
        private readonly IBankIdSupportedDeviceDetector _bankIdSupportedDeviceDetector;
        private readonly IBankIdLauncher _bankIdLauncher;
        private readonly IBankIdApiClient _bankIdApiClient;
        private readonly IBankIdOrderRefProtector _orderRefProtector;
        private readonly IBankIdLoginOptionsProtector _loginOptionsProtector;
        private readonly IBankIdLoginResultProtector _loginResultProtector;
        private readonly IBankIdQrCodeGenerator _qrCodeGenerator;
        private readonly IEndUserIpResolver _endUserIpResolver;
        private readonly IBankIdEventTrigger _bankIdEventTrigger;

        public BankIdApiController(
            UrlEncoder urlEncoder,
            IBankIdUserMessage bankIdUserMessage,
            IBankIdUserMessageLocalizer bankIdUserMessageLocalizer,
            IBankIdSupportedDeviceDetector bankIdSupportedDeviceDetector,
            IBankIdLauncher bankIdLauncher,
            IBankIdApiClient bankIdApiClient,
            IBankIdOrderRefProtector orderRefProtector,
            IBankIdLoginOptionsProtector loginOptionsProtector,
            IBankIdLoginResultProtector loginResultProtector,
            IBankIdQrCodeGenerator qrCodeGenerator,
            IEndUserIpResolver endUserIpResolver,
            IBankIdEventTrigger bankIdEventTrigger)
        {
            _urlEncoder = urlEncoder;
            _bankIdUserMessage = bankIdUserMessage;
            _bankIdUserMessageLocalizer = bankIdUserMessageLocalizer;
            _bankIdSupportedDeviceDetector = bankIdSupportedDeviceDetector;
            _bankIdLauncher = bankIdLauncher;
            _bankIdApiClient = bankIdApiClient;
            _orderRefProtector = orderRefProtector;
            _loginOptionsProtector = loginOptionsProtector;
            _loginResultProtector = loginResultProtector;
            _qrCodeGenerator = qrCodeGenerator;
            _endUserIpResolver = endUserIpResolver;
            _bankIdEventTrigger = bankIdEventTrigger;
        }

        [ValidateAntiForgeryToken]
        [HttpPost("Initialize")]
        public async Task<ActionResult<BankIdLoginApiInitializeResponse>> Initialize(BankIdLoginApiInitializeRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.LoginOptions))
            {
                throw new ArgumentNullException(nameof(request.LoginOptions));
            }

            if (string.IsNullOrWhiteSpace(request.ReturnUrl))
            {
                throw new ArgumentNullException(nameof(request.ReturnUrl));
            }

            var unprotectedLoginOptions = _loginOptionsProtector.Unprotect(request.LoginOptions);

            SwedishPersonalIdentityNumber? personalIdentityNumber = null;
            if (unprotectedLoginOptions.IsAutoLogin())
            {
                if (!unprotectedLoginOptions.AllowChangingPersonalIdentityNumber)
                {
                    personalIdentityNumber = unprotectedLoginOptions.PersonalIdentityNumber;
                }
            }
            else
            {
                if (!SwedishPersonalIdentityNumber.TryParse(request.PersonalIdentityNumber, out personalIdentityNumber))
                {
                    return BadRequestJsonResult(new
                    {
                        PersonalIdentityNumber = "Invalid PersonalIdentityNumber."
                    });
                }
            }

            var detectedUserDevice = GetDetectedUserDevice();
            AuthResponse authResponse;
            try
            {
                var authRequest = GetAuthRequest(personalIdentityNumber, unprotectedLoginOptions);
                authResponse = await _bankIdApiClient.AuthAsync(authRequest);
            }
            catch (BankIdApiException bankIdApiException)
            {
                await _bankIdEventTrigger.TriggerAsync(new BankIdAuthErrorEvent(personalIdentityNumber, bankIdApiException, detectedUserDevice));

                var errorStatusMessage = GetStatusMessage(bankIdApiException);
                return BadRequestJsonResult(new BankIdLoginApiErrorResponse(errorStatusMessage));
            }

            var orderRef = authResponse.OrderRef;
            var protectedOrderRef = _orderRefProtector.Protect(new BankIdOrderRef(orderRef));

            await _bankIdEventTrigger.TriggerAsync(new BankIdAuthSuccessEvent(personalIdentityNumber, orderRef, detectedUserDevice));

            if (unprotectedLoginOptions.AutoLaunch)
            {
                var launchInfo = GetBankIdLaunchInfo(request, authResponse);
                
                // Don't check for status if the browser will reload on return
                if (launchInfo.DeviceWillReloadPageOnReturnFromBankIdApp)
                {
                    return OkJsonResult(BankIdLoginApiInitializeResponse.AutoLaunch(protectedOrderRef, launchInfo.LaunchUrl, launchInfo.DeviceMightRequireUserInteractionToLaunchBankIdApp));
                }
                else
                {
                    return OkJsonResult(BankIdLoginApiInitializeResponse.AutoLaunchAndCheckStatus(protectedOrderRef, launchInfo.LaunchUrl, launchInfo.DeviceMightRequireUserInteractionToLaunchBankIdApp));
                }
            }

            if (unprotectedLoginOptions.UseQrCode)
            {
                var qrCode = _qrCodeGenerator.GenerateQrCodeAsBase64(authResponse.AutoStartToken);
                return OkJsonResult(BankIdLoginApiInitializeResponse.ManualLaunch(protectedOrderRef, qrCode));
            }

            return OkJsonResult(BankIdLoginApiInitializeResponse.ManualLaunch(protectedOrderRef));
        }
        
        private AuthRequest GetAuthRequest(SwedishPersonalIdentityNumber? personalIdentityNumber, BankIdLoginOptions loginOptions)
        {
            var endUserIp = _endUserIpResolver.GetEndUserIp(HttpContext);
            var personalIdentityNumberString = personalIdentityNumber?.To12DigitString();
            var autoStartTokenRequired = string.IsNullOrEmpty(personalIdentityNumberString) ? true : (bool?)null;

            List<string>? certificatePolicies = null;
            if (loginOptions.CertificatePolicies != null && loginOptions.CertificatePolicies.Any())
            {
                certificatePolicies = loginOptions.CertificatePolicies;
            }

            var authRequestRequirement = new Requirement(certificatePolicies, autoStartTokenRequired, loginOptions.AllowBiometric);

            return new AuthRequest(endUserIp, personalIdentityNumberString, authRequestRequirement);
        }

        private BankIdLaunchInfo GetBankIdLaunchInfo(BankIdLoginApiInitializeRequest request, AuthResponse authResponse)
        {
            var returnRedirectUri = GetAbsoluteUrl(Url.Action("Login", "BankId", new
            {
                returnUrl = request.ReturnUrl,
                loginOptions = request.LoginOptions
            }));
            var launchUrlRequest = new LaunchUrlRequest(returnRedirectUri, authResponse.AutoStartToken);
            return _bankIdLauncher.GetLaunchInfo(launchUrlRequest, HttpContext);
        }

        private string GetAbsoluteUrl(string returnUrl)
        {
            var absoluteUri = $"{Request.Scheme}://{Request.Host.ToUriComponent()}";
            return absoluteUri + returnUrl;
        }

        [ValidateAntiForgeryToken]
        [HttpPost("Status")]
        public async Task<ActionResult> Status(BankIdLoginApiStatusRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.LoginOptions))
            {
                throw new ArgumentNullException(nameof(request.LoginOptions));
            }

            if (string.IsNullOrWhiteSpace(request.ReturnUrl))
            {
                throw new ArgumentNullException(nameof(request.ReturnUrl));
            }

            if (string.IsNullOrWhiteSpace(request.OrderRef))
            {
                throw new ArgumentNullException(nameof(request.OrderRef));
            }

            var unprotectedLoginOptions = _loginOptionsProtector.Unprotect(request.LoginOptions);
            var orderRef = _orderRefProtector.Unprotect(request.OrderRef);

            CollectResponse collectResponse;
            try
            {
                collectResponse = await _bankIdApiClient.CollectAsync(orderRef.OrderRef);
            }
            catch (BankIdApiException bankIdApiException)
            {
                await _bankIdEventTrigger.TriggerAsync(new BankIdCollectErrorEvent(orderRef.OrderRef, bankIdApiException));
                var errorStatusMessage = GetStatusMessage(bankIdApiException);
                return BadRequestJsonResult(new BankIdLoginApiErrorResponse(errorStatusMessage));
            }

            var statusMessage = GetStatusMessage(collectResponse, unprotectedLoginOptions);

            if (collectResponse.GetCollectStatus() == CollectStatus.Pending)
            {
                return CollectPending(collectResponse, statusMessage);
            }

            if (collectResponse.GetCollectStatus() == CollectStatus.Complete)
            {
                return await CollectComplete(request, collectResponse);
            }

            var hintCode = collectResponse.GetCollectHintCode();
            if (hintCode.Equals(CollectHintCode.StartFailed)
                && request.AutoStartAttempts < BankIdConstants.MaxRetryLoginAttempts)
            {
                return OkJsonResult(BankIdLoginApiStatusResponse.Retry(statusMessage));
            }

            return CollectFailure(collectResponse, statusMessage);
        }

        private ActionResult CollectFailure(CollectResponse collectResponse, string statusMessage)
        {
            _bankIdEventTrigger.TriggerAsync(new BankIdCollectFailureEvent(collectResponse.OrderRef, collectResponse.GetCollectHintCode()));
            return BadRequestJsonResult(new BankIdLoginApiErrorResponse(statusMessage));
        }

        private async Task<ActionResult> CollectComplete(BankIdLoginApiStatusRequest request, CollectResponse collectResponse)
        {
            if (collectResponse.CompletionData == null)
            {
                throw new ArgumentNullException(nameof(collectResponse.CompletionData));
            }

            if (request.ReturnUrl == null)
            {
                throw new ArgumentNullException(nameof(request.ReturnUrl));
            }

            await _bankIdEventTrigger.TriggerAsync(new BankIdCollectCompletedEvent(collectResponse.OrderRef, collectResponse.CompletionData));

            var returnUri = GetSuccessReturnUri(collectResponse.CompletionData.User, request.ReturnUrl);
            if (!Url.IsLocalUrl(returnUri))
            {
                throw new Exception(BankIdConstants.InvalidReturnUrlErrorMessage);
            }

            return OkJsonResult(BankIdLoginApiStatusResponse.Finished(returnUri));
        }

        private ActionResult CollectPending(CollectResponse collectResponse, string statusMessage)
        {
            _bankIdEventTrigger.TriggerAsync(new BankIdCollectPendingEvent(collectResponse.OrderRef, collectResponse.GetCollectHintCode()));
            return OkJsonResult(BankIdLoginApiStatusResponse.Pending(statusMessage));
        }

        private string GetStatusMessage(CollectResponse collectResponse, BankIdLoginOptions unprotectedLoginOptions)
        {
            var authPersonalIdentityNumberProvided = PersonalIdentityNumberProvided(unprotectedLoginOptions);
            var detectedDevice = GetDetectedUserDevice();
            var accessedFromMobileDevice = detectedDevice.DeviceType == BankIdSupportedDeviceType.Mobile;
            var usingQrCode = unprotectedLoginOptions.UseQrCode;

            var messageShortName = _bankIdUserMessage.GetMessageShortNameForCollectResponse(
                collectResponse.GetCollectStatus(),
                collectResponse.GetCollectHintCode(),
                authPersonalIdentityNumberProvided,
                accessedFromMobileDevice,
                usingQrCode);
            var statusMessage = _bankIdUserMessageLocalizer.GetLocalizedString(messageShortName);

            return statusMessage;
        }

        private BankIdSupportedDevice GetDetectedUserDevice()
        {
            return _bankIdSupportedDeviceDetector.Detect(HttpContext.Request.Headers[UserAgentHttpHeaderName]);
        }

        private static bool PersonalIdentityNumberProvided(BankIdLoginOptions unprotectedLoginOptions)
        {
            return unprotectedLoginOptions.AllowChangingPersonalIdentityNumber
                   || unprotectedLoginOptions.PersonalIdentityNumber != null;
        }

        private string GetStatusMessage(BankIdApiException bankIdApiException)
        {
            var messageShortName = _bankIdUserMessage.GetMessageShortNameForErrorResponse(bankIdApiException.ErrorCode);
            var statusMessage = _bankIdUserMessageLocalizer.GetLocalizedString(messageShortName);

            return statusMessage;
        }

        private string GetSuccessReturnUri(User user, string returnUrl)
        {
            var loginResult = BankIdLoginResult.Success(user.PersonalIdentityNumber, user.Name, user.GivenName, user.Surname);
            var protectedLoginResult = _loginResultProtector.Protect(loginResult);
            var queryString = $"loginResult={_urlEncoder.Encode(protectedLoginResult)}";

            return AppendQueryString(returnUrl, queryString);
        }

        private static string AppendQueryString(string url, string queryString)
        {
            var delimiter = url.Contains("?") ? "&" : "?";
            return $"{url}{delimiter}{queryString}";
        }

        [ValidateAntiForgeryToken]
        [HttpPost("Cancel")]
        public async Task<ActionResult> Cancel(BankIdLoginApiCancelRequest request)
        {
            if (request.OrderRef == null)
            {
                throw new ArgumentNullException(nameof(request.OrderRef));
            }

            var orderRef = _orderRefProtector.Unprotect(request.OrderRef);

            try
            {
                await _bankIdApiClient.CancelAsync(orderRef.OrderRef);
                await _bankIdEventTrigger.TriggerAsync(new BankIdCancelSuccessEvent(orderRef.OrderRef));
            }
            catch (BankIdApiException exception)
            {
                // When we get exception in a cancellation request, chances
                // are that the orderRef has already been cancelled or we have
                // a network issue. We still want to provide the GUI with the
                // validated cancellation URL though.
                await _bankIdEventTrigger.TriggerAsync(new BankIdCancelErrorEvent(orderRef.OrderRef, exception));
            }

            return OkJsonResult(BankIdLoginApiCancelResponse.Cancelled());
        }

        private ActionResult OkJsonResult(object model)
        {
            return new JsonResult(model, BankIdConstants.JsonSerializerOptions)
            {
                StatusCode = StatusCodes.Status200OK
            };
        }

        private ActionResult BadRequestJsonResult(object model)
        {
            return new JsonResult(model, BankIdConstants.JsonSerializerOptions)
            {
                StatusCode = StatusCodes.Status400BadRequest
            };
        }
    }
}
