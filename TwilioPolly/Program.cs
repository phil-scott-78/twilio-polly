using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Polly;
using Polly.CircuitBreaker;
using Twilio;
using Twilio.Clients;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;

namespace TwilioPolly
{
    class Program
    {
        private static readonly Random Jitterer = new Random();

        public static async Task Main(string[] args)
        {
            // pull our account sid, auth token and the phone numbers from the usersecrets.json file
            // in a production app we'd obviously want to store these in a secure spot like our appSettings
            // or environmental variables.
            // see https://www.twilio.com/blog/2018/05/user-secrets-in-a-net-core-console-app.html
            // for more info on user secrets

            var builder = new ConfigurationBuilder();
            builder.AddUserSecrets<Program>();
            var config = builder.Build();

            var accountSid = config["twilio:accountSid"];
            var authToken = config["twilio:authToken"];
            var fromPhone = config["app:fromPhone"];
            var toPhone = config["app:toPhone"];

            TwilioClient.SetRestClient(new ChaosTwilioRestClient(new TwilioRestClient(accountSid, authToken), TimeSpan.FromSeconds(10)));

            var policy = PollyPolicies.TwilioCircuitBreakerWrappedInRetryPolicy;

            var message = await policy.ExecuteAsync(async () => await MessageResource.CreateAsync(
                body: "Coming to you live from a very chaotic world!",
                from: new Twilio.Types.PhoneNumber(fromPhone),
                to: new Twilio.Types.PhoneNumber(toPhone)
            ));

            Console.WriteLine($"Message sent! Sid: {message.Sid}");
            Console.ReadLine();
        }


        /*******************************************************************
         *
         * sample policies to try rather than the one defined in the
         * PollyPolicies file
         * 
         *******************************************************************/

        /// <summary>
        /// Get an exponential backoff plus jittered retry policy
        /// </summary>
        /// <remarks>In high-throughput scenarios, it can also be beneficial to add jitter to wait-and-retry strategies, to prevent retries bunching into further spikes of load.
        /// </remarks>
        /// <returns></returns>
        private static Policy GetJitteredRetryPolicy()
        {
            var retryPolicy = Policy.Handle<ApiConnectionException>()
                .WaitAndRetryAsync(DecorrelatedJitter(50, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5)),
                    onRetry: (exception, span, count, context) =>
                    {
                        Console.WriteLine(
                            $"Action failed with error of \"{exception.Message}\". Waiting {span.TotalMilliseconds}ms to retry (#{count})");
                    });

            return retryPolicy;
        }


        /// <summary>
        /// Gets a policy that in addition to checking for things like a socket exception also checks for specific HTTP codes that
        /// are worth retrying such as bad gateways or service unavailable
        /// </summary>
        /// <returns></returns>
        private static Policy GetRetryWithHttpResponseSmartsPolicy()
        {
            int[] httpStatusCodesWorthRetrying =
            {
                (int) HttpStatusCode.RequestTimeout, // 408
                (int) HttpStatusCode.InternalServerError, // 500
                (int) HttpStatusCode.BadGateway, // 502
                (int) HttpStatusCode.ServiceUnavailable, // 503
                (int) HttpStatusCode.GatewayTimeout // 504
            };

            var retryPolicy = Policy
                .Handle<ApiConnectionException>()
                .Or<ApiException>(exception => httpStatusCodesWorthRetrying.Contains(exception.Status))
                .WaitAndRetryForeverAsync(
                    sleepDurationProvider: i => TimeSpan.FromMilliseconds(250),
                    onRetry: (exception, span) =>
                        Console.WriteLine(
                            $"Action failed with error of \"{exception.Message}\". Waiting {span.TotalMilliseconds}ms to retry"));

            return retryPolicy;
        }

        /// <summary>
        /// Returns a polcy that will retry for ever, but if we receive 5 straight failures it will prevent
        /// calls to the service for 5 seconds
        /// </summary>
        /// <param name="exceptionsAllowedBeforeBreaking"></param>
        /// <param name="durationOfBreak"></param>
        /// <param name="retryDelay"></param>
        /// <returns></returns>
        private static Policy GetCircuitBreakerWrappedInRetryPolicy(int exceptionsAllowedBeforeBreaking, TimeSpan durationOfBreak, TimeSpan retryDelay)
        {
            var breakerPolicy = Policy
                .Handle<ApiConnectionException>()
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: exceptionsAllowedBeforeBreaking,
                    durationOfBreak: durationOfBreak,
                    onBreak: (exception, span) =>
                        Console.WriteLine($"Hold up, too many exceptions are being thrown. Not calling the service for {span}"),
                    onReset: () => Console.WriteLine("Success!, circuit breaker reset."),
                    onHalfOpen: () => Console.WriteLine("Circuit is half-opened. Going to test if we can call...")
                );

            var retryPolicy = Policy
                .Handle<ApiConnectionException>()
                .Or<BrokenCircuitException>()
                .WaitAndRetryForeverAsync(
                    sleepDurationProvider: retryAttempt => retryDelay,
                    onRetry: (exception, span) =>
                        Console.WriteLine(
                            $"Action failed with error of \"{exception.Message}\". Waiting {span.TotalMilliseconds}ms to retry"));

            var policy = Policy.WrapAsync(retryPolicy, breakerPolicy);
            return policy;
        }

        private static IEnumerable<TimeSpan> DecorrelatedJitter(int maxRetries, TimeSpan seedDelay, TimeSpan maxDelay)
        {
            var retries = 0;
            var seed = seedDelay.TotalMilliseconds;
            var max = maxDelay.TotalMilliseconds;
            var current = seed;

            while (++retries <= maxRetries)
            {
                // adopting the 'Decorrelated Jitter' formula from https://www.awsarchitectureblog.com/2015/03/backoff.html.
                // Can be between seed and previous * 3.  Mustn't exceed max.
                current = Math.Min(max, Math.Max(seed, current * 3 * Jitterer.NextDouble()));
                yield return TimeSpan.FromMilliseconds(current);
            }
        }
    }
}
