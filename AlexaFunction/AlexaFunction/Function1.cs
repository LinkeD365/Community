using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Microsoft.Azure.ServiceBus;
using System.Runtime.Serialization;
using System.Text;

namespace AlexaFunction
{
    public static class Function1
    {
        private static IQueueClient queueClient;
        [FunctionName("Alexa")]
        public static async Task<IActionResult> RunAlexa(
                        [HttpTrigger(AuthorizationLevel.Function, new string[] { "get", "post" }, Route = null)] HttpRequest req,
                        ILogger log,
                        ExecutionContext context)
        {
            log.LogInformation("Alexa HTTP Triggered");
            string content = new StreamReader(req.Body).ReadToEnd();
            dynamic alexaContent = JsonConvert.DeserializeObject(content);

            if (alexaContent.context.System.apiAccessToken == null)
            {
                log.LogError("No Access Token sent");
                return null;
            }
            string apiAccessToken = alexaContent.context.System.apiAccessToken;
            string intent = alexaContent.request.intent.name;
            var date = alexaContent.request.intent.slots.date != null ? alexaContent.request.intent.slots.date.value : string.Empty;
            var device = alexaContent.request.intent.slots.device != null ? alexaContent.request.intent.slots.device.value : string.Empty;
            // Get email from alexa
            var emailAddress = string.Empty;
            using (HttpClient client = new HttpClient())
            {
                client.BaseAddress = new Uri("https://api.eu.amazonalexa.com/v2/accounts/~current/settings/Profile.email");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiAccessToken);
                try
                {
                    emailAddress = await client.GetStringAsync("");
                    log.LogInformation(emailAddress);
                }
                catch (Exception ex)
                {
                    log.LogInformation(ex.ToString());
                    var errorObject = new
                    {
                        version = "1.0",
                        response = new
                        {
                            card = new
                            {
                                type = "AskForPermissionsConsent",
                                permissions = new string[1]
                          {
                  "alexa::profile:email:read"
                          }
                            }
                        }
                    };
                    return new JsonResult(errorObject);
                }
            }

            emailAddress = emailAddress.Substring(1, emailAddress.Length - 2);
            log.LogInformation("Getting App Values");
            string baseUrl = Environment.GetEnvironmentVariable("baseUrl");
            string clientId = Environment.GetEnvironmentVariable("clientId");
            string secret = Environment.GetEnvironmentVariable("secret");
            string authority = Environment.GetEnvironmentVariable("Authority");
            log.LogInformation(baseUrl + "|" + clientId + "|" + secret + "|" + authority + "|");
            log.LogInformation("Getting token");
            string getToken = await GetToken(baseUrl, clientId, secret, authority, log);

            log.LogInformation("Token: " + getToken.ToString());
            TokenResponse tokenResponse = JsonConvert.DeserializeObject<TokenResponse>(getToken);
            string _accessToken = tokenResponse.access_token;
            log.LogInformation("Got Token");
            using (HttpClient d365Connect = new HttpClient())
            {
                d365Connect.BaseAddress = new Uri(baseUrl);
                d365Connect.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
                d365Connect.DefaultRequestHeaders.Add("OData-Version", "4.0");
                d365Connect.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                d365Connect.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                HttpResponseMessage whoAmIResponse = await d365Connect.GetAsync("api/data/v9.1//contacts()?$select=firstname, lastname&$filter=emailaddress1 eq '" + emailAddress + "'");
                if (!whoAmIResponse.IsSuccessStatusCode)
                    return (IActionResult)null;
                log.LogInformation("Read Contact");
                JObject jWhoAmIResponse = JObject.Parse(whoAmIResponse.Content.ReadAsStringAsync().Result);
                if (!jWhoAmIResponse["value"].HasValues)
                {
                    log.LogInformation("Cant find contact");
                    var errorObject = new
                    {
                        version = "1.0",
                        response = new
                        {
                            outputSpeech = new
                            {
                                text = "Hi, Big Energy Co here. Unfortunately," + emailAddress + "is not registered with us for the Alexa App. Please contact 01234 567890 during office hours to configure your account",
                                type = "PlainText"
                            },
                            card = new
                            {
                                type = "Standard",
                                title = "Big Energy Co",
                                text = "Unfortunately, " + emailAddress + "is not registered with us for the Alexa App. Please contact 01234 567890 during office hours to configure your account"
                            }
                        }
                    };
                    return new JsonResult(errorObject);
                }
                JToken firstname = jWhoAmIResponse["value"][0]["firstname"];
                log.LogInformation("Before send to bus");
                var queueClient = (IQueueClient)new QueueClient(Environment.GetEnvironmentVariable("ServiceBusConString"), Environment.GetEnvironmentVariable("queueName"), ReceiveMode.PeekLock, (RetryPolicy)null);
                var fsObject = new
                {
                    email = emailAddress,
                    contactId = (Guid)jWhoAmIResponse["value"][0]["contactid"],
                    intent,
                    date,
                    device
                };
                Task messageReturn = SendMessagesAsync(JsonConvert.SerializeObject(fsObject).ToString());
                log.LogInformation("Sent to bus");
                string returnBody = string.Empty;

                switch (intent)
                {
                    case "service":
                        returnBody = "Hi, we have recieved your request for an Azure Service ";
                        returnBody += device == "" ? "" : " for your " + device;
                        returnBody += date == string.Empty ? "" : ". We will endeavour to book a service on " + date;
                        break;
                    case "repair":
                        returnBody = "Sorry to hear you have a Azure broken ";
                        returnBody += device == string.Empty ? "device" : device;
                        returnBody += date == string.Empty ? "" : ". We will endeavor to send an engineer on " + date;
                        break;
                    case "emergency":
                        returnBody = "Oh No! An Azure Emergency!";
                        break;
                    default:
                        returnBody = "OK, Big Energy Azure Function has got your request";
  
                        break;
                }

                returnBody += ". One of our support specialist will contact you within the hour to confirm the scheduled time for our engineer";
                var returnObject = new
                {
                    version = "1.0",
                    response = new
                    {
                        outputSpeech = new
                        {
                            text = returnBody,
                            type = "PlainText"
                        },
                        card = new
                        {
                            type = "Standard",
                            title = "Big Energy Co",
                            text = returnBody
                        }
                    }
                };
                log.LogInformation("Responding");
                return (IActionResult)new JsonResult(returnObject);
            }
        }
        private static async Task<string> GetToken(
              string baseUrl,
              string clientId,
              string secret,
              string Authority,
              ILogger log)
        {
            log.LogInformation("In GetToken");
            string str;
            using (HttpClient httpClient = new HttpClient())
            {
                FormUrlEncodedContent formContent = new FormUrlEncodedContent((IEnumerable<KeyValuePair<string, string>>)new KeyValuePair<string, string>[4]
                {
                  new KeyValuePair<string, string>("resource", baseUrl),
                  new KeyValuePair<string, string>("client_id", clientId),
                  new KeyValuePair<string, string>("client_secret", secret),
                  new KeyValuePair<string, string>("grant_type", "client_credentials")
                });
                log.LogInformation("postsync||" + formContent.ToString() + "||" + Authority);
                HttpResponseMessage response = await httpClient.PostAsync(Authority, (HttpContent)formContent);
                log.LogInformation(response.ToString());
                str = !response.IsSuccessStatusCode ? (string)null : response.Content.ReadAsStringAsync().Result;
            }
            return str;
        }

        private static async Task SendMessagesAsync(string messageToSend)
        {
            try
            {
                Message message = new Message(Encoding.UTF8.GetBytes(messageToSend));
                Console.WriteLine("Sending message: " + messageToSend);
                await queueClient.SendAsync(message);
                message = (Message)null;
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("{0} :: Exception: {1}", DateTime.Now, ex.Message));
            }
        }
        [DataContract]
        public class TokenResponse
        {
            [DataMember]
            public string token_type { get; set; }

            [DataMember]
            public string expires_in { get; set; }

            [DataMember]
            public string ext_expires_in { get; set; }

            [DataMember]
            public string expires_on { get; set; }

            [DataMember]
            public string not_before { get; set; }

            [DataMember]
            public string resource { get; set; }

            [DataMember]
            public string access_token { get; set; }
        }
    }
}
