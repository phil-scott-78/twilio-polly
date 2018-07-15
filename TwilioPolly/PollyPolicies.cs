using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Polly;
using Polly.CircuitBreaker;
using Twilio.Exceptions;

namespace TwilioPolly
{
    /// <summary>
    /// Example of something that would be more production ready for configuring your
    /// policy instances. Retry policies are thread-safe as are the circuit breaker
    /// so we can store and reuse them from a central location.
    /// </summary>
    class PollyPolicies
    {
        private static readonly Random Jitterer = new Random();

        private static readonly int[] HttpStatusCodesWorthRetrying =
        {
            (int) HttpStatusCode.RequestTimeout, // 408
            (int) HttpStatusCode.InternalServerError, // 500
            (int) HttpStatusCode.BadGateway, // 502
            (int) HttpStatusCode.ServiceUnavailable, // 503
            (int) HttpStatusCode.GatewayTimeout // 504
        };

        /// <summary>
        /// Retry policy with special handling for ApiConnectionExceptions and ApiExceptions
        /// with specific return codes which are retriable
        /// </summary>
        public static readonly Policy TwilioRetryPolicy = Policy
            .Handle<ApiConnectionException>()
            .Or<ApiException>(exception => HttpStatusCodesWorthRetrying.Contains(exception.Status))
            .Or<BrokenCircuitException>()
            .WaitAndRetryAsync(DecorrelatedJitter(50, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5)),
                onRetry: (exception, span, count, context) =>
                {
                    Console.WriteLine(
                        $"Action failed with error of \"{exception.Message}\". Waiting {span.TotalMilliseconds}ms to retry (#{count})");
                });

        /// <summary>
        /// Circuit breaker for calling twilio.
        /// </summary>
        /// <remarks>
        /// There isn't anything Twilio specific here, but we want a single instance
        /// of a circuit breaker for working against the twilio service so it has a single copy
        /// of the exceptions allowed between breaks
        /// </remarks>
        public static readonly Policy TwilioBreakerPolicy = Policy
            .Handle<ApiConnectionException>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(5),
                onBreak: (exception, span) =>
                    Console.WriteLine($"Hold up, too many exceptions are being thrown. Not calling the service for {span}"),
                onReset: () => Console.WriteLine("Success!, circuit breaker reset."),
                onHalfOpen: () => Console.WriteLine("Circuit is half-opened. Going to test if we can call...")
            );

        public static Policy TwilioCircuitBreakerWrappedInRetryPolicy = Policy.WrapAsync(TwilioRetryPolicy, TwilioBreakerPolicy);

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
