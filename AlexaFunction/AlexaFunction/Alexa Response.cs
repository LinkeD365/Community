using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace AlexaFunction
{
    public static partial class Alexa
    {
        private static IQueueClient queueClient;
        [FunctionName("Alexa")]
        public static async Task<IActionResult> RunAlexa(
                        [HttpTrigger(AuthorizationLevel.Function, new string[] { "get", "post" }, Route = null)] HttpRequest req,
                        ILogger log,
                        ExecutionContext context)
        {
            log.LogInformation("Alexa HTTP Triggered");
            log.LogInformation(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString());
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

            log.LogInformation("Got Token");
            using (HttpClient d365Connect = new HttpClient())
            {
                d365Connect.BaseAddress = new Uri(baseUrl);
                d365Connect.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
                d365Connect.DefaultRequestHeaders.Add("OData-Version", "4.0");
                d365Connect.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                d365Connect.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", getToken);
                HttpResponseMessage contactResponse = await d365Connect.GetAsync("api/data/v9.1//contacts()?$select=firstname, lastname&$filter=emailaddress1 eq '" + emailAddress + "'");
                if (!contactResponse.IsSuccessStatusCode)
                    return null;
                log.LogInformation("Read Contact");
                JObject jContactResponse = JObject.Parse(contactResponse.Content.ReadAsStringAsync().Result);
                if (!jContactResponse["value"].HasValues)
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
                JToken firstname = jContactResponse["value"][0]["firstname"];
                log.LogInformation("Before send to bus");
                var queueClient = (IQueueClient)new QueueClient(Environment.GetEnvironmentVariable("ServiceBusConString"), Environment.GetEnvironmentVariable("queueName"), ReceiveMode.PeekLock, null);
                var fsObject = new
                {
                    email = emailAddress,
                    contactId = (Guid)jContactResponse["value"][0]["contactid"],
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
                return new JsonResult(returnObject);
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

            AuthenticationContext authContext = new AuthenticationContext(Authority);

            ClientCredential credential = new ClientCredential(clientId, secret);

            AuthenticationResult result = await authContext.AcquireTokenAsync(baseUrl, credential);

            return result.AccessToken;
        }

        private static async Task SendMessagesAsync(string messageToSend)
        {
            try
            {
                Message message = new Message(Encoding.UTF8.GetBytes(messageToSend));
                Console.WriteLine("Sending message: " + messageToSend);
                await queueClient.SendAsync(message);
                message = null;
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
