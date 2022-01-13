using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json;

namespace ShippedItemInstanceConverter
{
    public class Converter
    {
        private readonly ILogger<Converter> _logger;

        public Converter(ILogger<Converter> log)
        {
            _logger = log;
        }

        [FunctionName("Converter")]
        [OpenApiOperation(operationId: "Run", tags: new[] { "name" })]
        [OpenApiSecurity("function_key", SecuritySchemeType.ApiKey, Name = "code", In = OpenApiSecurityLocationType.Query)]
        [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "text/plain", bodyType: typeof(string), Description = "The OK response")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req, ExecutionContext executionContext)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");

            bool nativeOut = false;
            bool isoOut = false;
            bool admOut = false;
            if (req.Headers.TryGetValue("content-type", out Microsoft.Extensions.Primitives.StringValues contentHeaders))
            {
                //Determine output type from header
                if (contentHeaders.Contains("application/vnd.aggateway.adapt.iso+zip"))
                {
                    isoOut = true;
                }
                else if (contentHeaders.Contains("application/vnd.aggateway.adapt.adm+zip"))
                {
                    admOut = true;
                }
                else
                {
                    nativeOut = true;
                }

                //Read the input shipped item instance input from the request body
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                string input = JsonConvert.DeserializeObject(requestBody).ToString();
                input = input.Replace("\r\n", string.Empty).Replace("\t", string.Empty).Replace(" ", string.Empty); //Remove whitespace in the request as desired

                //Override the default location of the ADAPT resource files to accomodate placement within Azure function   
                AgGateway.ADAPT.Representation.UnitSystem.UnitSystemManager.UnitSystemDataLocation = System.IO.Path.Combine(executionContext.FunctionDirectory, "../Resources", "UnitSystem.xml");
                AgGateway.ADAPT.Representation.RepresentationSystem.RepresentationManager.RepresentationSystemDataLocation = System.IO.Path.Combine(executionContext.FunctionDirectory, "../Resources", "RepresentationSystem.xml");
                AgGateway.ADAPT.ISOv4Plugin.Representation.DdiLoader.DDIDataFile = System.IO.Path.Combine(executionContext.FunctionDirectory, "../Resources", "ddiExport.txt");
                AgGateway.ADAPT.ISOv4Plugin.Representation.IsoUnitOfMeasureList.ISOUOMDataFile = System.IO.Path.Combine(executionContext.FunctionDirectory, "../Resources", "IsoUnitOfMeasure.xml");

                //Write the input to a file so that the ShippedItemInstance plugin can read it
                string folder = System.IO.Path.GetTempPath();
                string tempPath = Path.Combine(folder, "input.json");
                File.WriteAllText(tempPath, input);

                //Read the input data
                AgGateway.ADAPT.ShippedItemInstancePlugin.Plugin p = new AgGateway.ADAPT.ShippedItemInstancePlugin.Plugin();
                var admList = p.Import(folder);

                if (admList.Count == 1) //We assume the caller is requesting one document at a time
                {
                    var inputData = admList[0];

                    string outputPath = Path.Combine(folder, "output");
                    if (Directory.Exists(outputPath))
                    {
                        Directory.Delete(outputPath, true);
                    }
                    Directory.CreateDirectory(outputPath);

                    string outputZip = Path.Combine(folder, "output.zip");
                    if (File.Exists(outputZip))
                    {
                        File.Delete(outputZip);
                    }

                    if (nativeOut)
                    {
                        return new OkObjectResult(input); //Just returning the json in the body 
                    }
                    else
                    {
                        if (isoOut)
                        {
                            AgGateway.ADAPT.ISOv4Plugin.Plugin isoPlugin = new AgGateway.ADAPT.ISOv4Plugin.Plugin();
                            isoPlugin.Export(inputData, outputPath, new AgGateway.ADAPT.ApplicationDataModel.ADM.Properties());
                        }
                        else if (admOut)
                        {
                            AgGateway.ADAPT.ADMPlugin.Plugin admPlugin = new AgGateway.ADAPT.ADMPlugin.Plugin();
                            admPlugin.Export(inputData, outputPath);
                        }

                        System.IO.Compression.ZipFile.CreateFromDirectory(outputPath, outputZip);
                        return new FileContentResult(File.ReadAllBytes(outputZip), "application/octet-stream");
                    }
                }
                else //Multiple documents not supported at this time.  Otherwise input not valid.
                {
                    return new BadRequestResult();
                }
            }
            return new BadRequestResult();   //Likely no valid content header or malformed request body      
        }
    }
}

