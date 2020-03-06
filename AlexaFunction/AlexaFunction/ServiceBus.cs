using Microsoft.Azure.WebJobs;

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;

namespace AlexaFunction
{
    public static partial class Alexa
    {
        [FunctionName("AddToFS")]
        public static async void AddToFS([ServiceBusTrigger("ccalexa", Connection = "servicequeue")] string myQueueItem, ILogger log, ExecutionContext context)
        {
            try
            {
                object request = JsonConvert.DeserializeObject(myQueueItem);
                log.LogInformation("Getting App Values");
                string baseUrl = Environment.GetEnvironmentVariable("baseUrl");
                string clientId = Environment.GetEnvironmentVariable("clientId");
                string secret = Environment.GetEnvironmentVariable("secret");
                string authority = Environment.GetEnvironmentVariable("Authority");
                log.LogInformation(baseUrl + "|" + clientId + "|" + secret + "|" + authority + "|");
                log.LogInformation("Getting tokecn");
                string getToken = await GetToken(baseUrl, clientId, secret, authority, log);
                TokenResponse tokenResponse = JsonConvert.DeserializeObject<TokenResponse>(getToken);
                log.LogInformation("got token");
                string accessToken = tokenResponse.access_token;
                using (HttpClient d365Connect = new HttpClient())
                {
                    d365Connect.BaseAddress = new Uri(baseUrl);
                    d365Connect.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
                    d365Connect.DefaultRequestHeaders.Add("OData-Version", "4.0");
                    d365Connect.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    d365Connect.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }   
            }
}
