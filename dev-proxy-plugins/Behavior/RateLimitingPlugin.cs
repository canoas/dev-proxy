﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.DevProxy.Abstractions;
using Microsoft.DevProxy.Plugins.MockResponses;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Microsoft.DevProxy.Plugins.Behavior;

public enum RateLimitResponseWhenLimitExceeded
{
    Throttle,
    Custom
}

public enum RateLimitResetFormat
{
    SecondsLeft,
    UtcEpochSeconds
}

public class RateLimitConfiguration
{
    public string HeaderLimit { get; set; } = "RateLimit-Limit";
    public string HeaderRemaining { get; set; } = "RateLimit-Remaining";
    public string HeaderReset { get; set; } = "RateLimit-Reset";
    public string HeaderRetryAfter { get; set; } = "Retry-After";
    public RateLimitResetFormat ResetFormat { get; set; } = RateLimitResetFormat.SecondsLeft;
    public int CostPerRequest { get; set; } = 2;
    public int ResetTimeWindowSeconds { get; set; } = 60;
    public int WarningThresholdPercent { get; set; } = 80;
    public int RateLimit { get; set; } = 120;
    public int RetryAfterSeconds { get; set; } = 5;
    public RateLimitResponseWhenLimitExceeded WhenLimitExceeded { get; set; } = RateLimitResponseWhenLimitExceeded.Throttle;
    public string CustomResponseFile { get; set; } = "rate-limit-response.json";
    public MockResponse? CustomResponse { get; set; }
}

public class RateLimitingPlugin : BaseProxyPlugin
{
    public override string Name => nameof(RateLimitingPlugin);
    private readonly RateLimitConfiguration _configuration = new();
    // initial values so that we know when we intercept the
    // first request and can set the initial values
    private int _resourcesRemaining = -1;
    private DateTime _resetTime = DateTime.MinValue;
    private RateLimitingCustomResponseLoader? _loader = null;

    private ThrottlingInfo ShouldThrottle(Request request, string throttlingKey)
    {
        var throttleKeyForRequest = BuildThrottleKey(request);
        return new ThrottlingInfo(throttleKeyForRequest == throttlingKey ? _configuration.RetryAfterSeconds : 0, _configuration.HeaderRetryAfter);
    }

    private void ThrottleResponse(ProxyRequestArgs e) => UpdateProxyResponse(e, HttpStatusCode.TooManyRequests);

    private void UpdateProxyResponse(ProxyHttpEventArgsBase e, HttpStatusCode errorStatus)
    {
        var headers = new List<HttpHeader>();
        var body = string.Empty;
        var request = e.Session.HttpClient.Request;
        var response = e.Session.HttpClient.Response;

        // resources exceeded
        if (errorStatus == HttpStatusCode.TooManyRequests)
        {
            if (ProxyUtils.IsGraphRequest(request))
            {
                string requestId = Guid.NewGuid().ToString();
                string requestDate = DateTime.Now.ToString();
                headers.AddRange(ProxyUtils.BuildGraphResponseHeaders(request, requestId, requestDate));

                body = JsonSerializer.Serialize(new GraphErrorResponseBody(
                    new GraphErrorResponseError
                    {
                        Code = new Regex("([A-Z])").Replace(errorStatus.ToString(), m => { return $" {m.Groups[1]}"; }).Trim(),
                        Message = BuildApiErrorMessage(request),
                        InnerError = new GraphErrorResponseInnerError
                        {
                            RequestId = requestId,
                            Date = requestDate
                        }
                    })
                );
            }

            headers.Add(new HttpHeader(_configuration.HeaderRetryAfter, _configuration.RetryAfterSeconds.ToString()));

            e.Session.GenericResponse(body ?? string.Empty, errorStatus, headers);
            return;
        }

        // add rate limiting headers if reached the threshold percentage
        if (_resourcesRemaining <= _configuration.RateLimit - (_configuration.RateLimit * _configuration.WarningThresholdPercent / 100))
        {
            var reset = _configuration.ResetFormat == RateLimitResetFormat.SecondsLeft ?
                (_resetTime - DateTime.Now).TotalSeconds.ToString("N0") :  // drop decimals
                new DateTimeOffset(_resetTime).ToUnixTimeSeconds().ToString();
            headers.AddRange(new List<HttpHeader> {
                new HttpHeader(_configuration.HeaderLimit, _configuration.RateLimit.ToString()),
                new HttpHeader(_configuration.HeaderRemaining, _resourcesRemaining.ToString()),
                new HttpHeader(_configuration.HeaderReset, reset)
            });

            // make rate limiting information available for CORS requests
            if (request.Headers.FirstOrDefault((h) => h.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) is not null)
            {
                if (!response.Headers.HeaderExists("Access-Control-Allow-Origin"))
                {
                    headers.Add(new HttpHeader("Access-Control-Allow-Origin", "*"));
                }
                var exposeHeadersHeader = response.Headers.FirstOrDefault((h) => h.Name.Equals("Access-Control-Expose-Headers", StringComparison.OrdinalIgnoreCase));
                var headerValue = "";
                if (exposeHeadersHeader is null)
                {
                    headerValue = $"{_configuration.HeaderLimit}, {_configuration.HeaderRemaining}, {_configuration.HeaderReset}, {_configuration.HeaderRetryAfter}";
                }
                else
                {
                    headerValue = exposeHeadersHeader.Value;
                    if (!headerValue.Contains(_configuration.HeaderLimit))
                    {
                        headerValue += $", {_configuration.HeaderLimit}";
                    }
                    if (!headerValue.Contains(_configuration.HeaderRemaining))
                    {
                        headerValue += $", {_configuration.HeaderRemaining}";
                    }
                    if (!headerValue.Contains(_configuration.HeaderReset))
                    {
                        headerValue += $", {_configuration.HeaderReset}";
                    }
                    if (!headerValue.Contains(_configuration.HeaderRetryAfter))
                    {
                        headerValue += $", {_configuration.HeaderRetryAfter}";
                    }
                    response.Headers.RemoveHeader("Access-Control-Expose-Headers");
                }

                headers.Add(new HttpHeader("Access-Control-Expose-Headers", headerValue));
            }
        }

        // add headers to the original API response, avoiding duplicates
        headers.ForEach(h => e.Session.HttpClient.Response.Headers.RemoveHeader(h.Name));
        e.Session.HttpClient.Response.Headers.AddHeaders(headers);
    }
    private static string BuildApiErrorMessage(Request r) => $"Some error was generated by the proxy. {(ProxyUtils.IsGraphRequest(r) ? ProxyUtils.IsSdkRequest(r) ? "" : String.Join(' ', MessageUtils.BuildUseSdkForErrorsMessage(r)) : "")}";

    private string BuildThrottleKey(Request r)
    {
        if (ProxyUtils.IsGraphRequest(r))
        {
            return GraphUtils.BuildThrottleKey(r);
        }
        else
        {
            return r.RequestUri.Host;
        }
    }

    public override void Register(IPluginEvents pluginEvents,
                         IProxyContext context,
                         ISet<UrlToWatch> urlsToWatch,
                         IConfigurationSection? configSection = null)
    {
        base.Register(pluginEvents, context, urlsToWatch, configSection);

        configSection?.Bind(_configuration);
        _loader = new RateLimitingCustomResponseLoader(_logger!, _configuration);
        // load the responses from the configured mocks file
        _loader.InitResponsesWatcher();

        pluginEvents.BeforeRequest += OnRequest;
        pluginEvents.BeforeResponse += OnResponse;
    }

    // add rate limiting headers to the response from the API
    private async Task OnResponse(object? sender, ProxyResponseArgs e)
    {
        if (_urlsToWatch is null ||
            !e.HasRequestUrlMatch(_urlsToWatch))
        {
            return;
        }

        UpdateProxyResponse(e, HttpStatusCode.OK);
    }

    private async Task OnRequest(object? sender, ProxyRequestArgs e)
    {
        var session = e.Session;
        var state = e.ResponseState;
        if (e.ResponseState.HasBeenSet ||
            _urlsToWatch is null ||
            !e.ShouldExecute(_urlsToWatch))
        {
            return;
        }

        // set the initial values for the first request
        if (_resetTime == DateTime.MinValue)
        {
            _resetTime = DateTime.Now.AddSeconds(_configuration.ResetTimeWindowSeconds);
        }
        if (_resourcesRemaining == -1)
        {
            _resourcesRemaining = _configuration.RateLimit;
        }

        // see if we passed the reset time window
        if (DateTime.Now > _resetTime)
        {
            _resourcesRemaining = _configuration.RateLimit;
            _resetTime = DateTime.Now.AddSeconds(_configuration.ResetTimeWindowSeconds);
        }

        // subtract the cost of the request
        _resourcesRemaining -= _configuration.CostPerRequest;
        if (_resourcesRemaining < 0)
        {
            _resourcesRemaining = 0;
            var request = e.Session.HttpClient.Request;

            _logger?.LogRequest(new[] { $"Exceeded resource limit when calling {request.Url}.", "Request will be throttled" }, MessageType.Failed, new LoggingContext(e.Session));
            if (_configuration.WhenLimitExceeded == RateLimitResponseWhenLimitExceeded.Throttle) {
                e.ThrottledRequests.Add(new ThrottlerInfo(
                    BuildThrottleKey(request),
                    ShouldThrottle,
                    DateTime.Now.AddSeconds(_configuration.RetryAfterSeconds)
                ));
                ThrottleResponse(e);
                state.HasBeenSet = true;
            }
            else
            {
                if (_configuration.CustomResponse is not null)
                {
                    var headers = _configuration.CustomResponse.ResponseHeaders is not null ?
                        _configuration.CustomResponse.ResponseHeaders.Select(h => new HttpHeader(h.Key, h.Value)) :
                        Array.Empty<HttpHeader>();

                    // allow custom throttling response
                    var responseCode = (HttpStatusCode)(_configuration.CustomResponse.ResponseCode ?? 200);
                    if (responseCode == HttpStatusCode.TooManyRequests) {
                        e.ThrottledRequests.Add(new ThrottlerInfo(
                            BuildThrottleKey(request),
                            ShouldThrottle,
                            DateTime.Now.AddSeconds(_configuration.RetryAfterSeconds)
                        ));
                    }

                    string body = _configuration.CustomResponse.ResponseBody is not null ?
                        JsonSerializer.Serialize(_configuration.CustomResponse.ResponseBody, new JsonSerializerOptions { WriteIndented = true }) :
                        "";
                    e.Session.GenericResponse(body, responseCode, headers);
                    state.HasBeenSet = true;
                }
                else
                {
                    _logger?.LogRequest(new[] { $"Custom behavior not set {_configuration.CustomResponseFile} not found." }, MessageType.Failed, new LoggingContext(e.Session));
                }
            }
        }
    }
}
