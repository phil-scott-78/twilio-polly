using System;
using System.Threading.Tasks;
using Twilio.Clients;
using Twilio.Exceptions;
using Twilio.Http;

namespace TwilioPolly
{
    class ChaosTwilioRestClient : ITwilioRestClient
    {
        private readonly TwilioRestClient _innerClient;
        private readonly DateTimeOffset _okAfterTime;

        private void CheckFail()
        {
            if (_okAfterTime <= DateTimeOffset.Now) return;
            throw new ApiConnectionException("kablooey!");
        }

        public ChaosTwilioRestClient(TwilioRestClient innerClient, TimeSpan howLongToFail)
        {
            _innerClient = innerClient;
            _okAfterTime = DateTimeOffset.Now.Add(howLongToFail);
        }

        public Response Request(Request request)
        {
            Console.WriteLine("Making HTTP request");
            CheckFail();
            return _innerClient.Request(request);
        }

        public Task<Response> RequestAsync(Request request)
        {
            Console.WriteLine("Making HTTP request");
            CheckFail();
            return _innerClient.RequestAsync(request);
        }

        public HttpClient HttpClient => _innerClient.HttpClient;

        public string AccountSid => _innerClient.AccountSid;

        public string Region => _innerClient.Region;
    }
}