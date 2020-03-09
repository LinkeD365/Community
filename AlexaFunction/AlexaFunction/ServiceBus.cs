using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace AlexaFunction
{
    public static partial class Alexa
    {
        [FunctionName("AddToFS")]
        public static async void AddToFS([ServiceBusTrigger("ccalexa", Connection = "ServiceBusConString")] string myQueueItem, ILogger log, ExecutionContext context)
        {
            try
            {
                log.LogInformation(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString());
                object request = JsonConvert.DeserializeObject(myQueueItem);
                log.LogInformation("Getting App Values");
                string baseUrl = Environment.GetEnvironmentVariable("baseUrl");
                string clientId = Environment.GetEnvironmentVariable("clientId");
                string secret = Environment.GetEnvironmentVariable("secret");
                string authority = Environment.GetEnvironmentVariable("Authority");
                log.LogInformation(baseUrl + "|" + clientId + "|" + secret + "|" + authority + "|");
                log.LogInformation("Getting tokecn");
                string getToken = await GetToken(baseUrl, clientId, secret, authority, log);
                log.LogInformation("got token");

                using (HttpClient d365Connect = new HttpClient())
                {
                    d365Connect.BaseAddress = new Uri(baseUrl);
                    d365Connect.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
                    d365Connect.DefaultRequestHeaders.Add("OData-Version", "4.0");
                    d365Connect.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    d365Connect.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", getToken);

                    JObject woObject = JObject.Parse(myQueueItem);

                    Guid contactId = (Guid)woObject["contactId"];
                    var email = woObject["email"];
                    var intent = woObject["intent"];
                    string date = (string)woObject["date"];
                    string device =  woObject["device"].ToString();

                    var contactResult = await d365Connect.GetAsync("api/data/v9.1/contacts(" + contactId + ")?$select=_parentcustomerid_value");
                    if (!contactResult.IsSuccessStatusCode)
                    {
                        return;
                    }

                    JObject contactObject = JObject.Parse(contactResult.Content.ReadAsStringAsync().Result);

                    var accountId = contactObject["_parentcustomerid_value"];

                    HttpResponseMessage accountResponse = await d365Connect.GetAsync("api/data/v9.1/accounts(" + accountId.ToString() + ")?$select=_defaultpricelevelid_value");
                    JObject jaccountResponse = JObject.Parse(accountResponse.Content.ReadAsStringAsync().Result);
                    Guid priceListId = (Guid)jaccountResponse["_defaultpricelevelid_value"];

                    HttpResponseMessage woTypeResponse = await d365Connect.GetAsync("api/data/v9.1/msdyn_workordertypes()?$select=msdyn_workordertypeid&$filter=cc_alexaintent eq '" + intent + "'");
                    JObject jwotResponse = JObject.Parse(woTypeResponse.Content.ReadAsStringAsync().Result);
                    Guid woTypeId = (Guid)jwotResponse["value"][0]["msdyn_workordertypeid"];

                    log.LogInformation("Got all the details preparing jobject");
                    JObject workOrder = new JObject();
                    workOrder.Add("msdyn_pricelist@odata.bind", ("/pricelevels(" + priceListId + ")"));
                    workOrder.Add("msdyn_name", ("AZ" + new Random().Next(4000, 500000)));
                    workOrder.Add("msdyn_serviceaccount@odata.bind", ("/accounts(" + accountId + ")"));
                    workOrder.Add("msdyn_systemstatus", 690970000);
                    workOrder.Add("msdyn_workordertype@odata.bind", ("/msdyn_workordertypes(" + woTypeId + ")"));
                    workOrder.Add("msdyn_taxable", false);
                    if (date != string.Empty) workOrder.Add("msdyn_timefrompromised", date);
                    if (device != string.Empty) workOrder.Add("msdyn_instructions", device);
                    log.LogInformation(workOrder.ToString());
                    HttpRequestMessage createWO = new HttpRequestMessage(HttpMethod.Post, d365Connect.BaseAddress.ToString() + "api/data/v9.1/msdyn_workorders");
                    createWO.Content = new StringContent(workOrder.ToString(), Encoding.UTF8, "application/json");
                    HttpResponseMessage createWOResp = d365Connect.SendAsync(createWO, HttpCompletionOption.ResponseContentRead).Result;
                    log.LogInformation("created WO");
                }
            }
            catch
            { }
        }
    }
}
