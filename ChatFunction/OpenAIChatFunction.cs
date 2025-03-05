using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using DocumentFormat.OpenXml.EMMA;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Images;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Logging;

public class OpenAIChatFunction
{
    private readonly ILogger<OpenAIChatFunction> _logger;
    private readonly AzureOpenAIClient _azureClient;
    private readonly AzureOpenAIClient _azureImageClient;
    private readonly ChatClient _chatClient;
    private readonly string _imageDeploymentName;

    public OpenAIChatFunction(ILogger<OpenAIChatFunction> logger)
    {
        _logger = logger;

        // Read Azure OpenAI settings from environment variables
        var endpoint = Environment.GetEnvironmentVariable("AzureOpenAIEndpoint") ?? throw new Exception("Azure OpenAI Endpoint not found.");
        var imageEndpoint = Environment.GetEnvironmentVariable("AzureOpenAIImageEndpoint") ?? throw new Exception("Azure OpenAI Endpoint not found.");
        var deploymentName = Environment.GetEnvironmentVariable("AzureOpenAIDeployment") ?? "gpt-35-turbo-16k";
        _imageDeploymentName = Environment.GetEnvironmentVariable("AzureOpenAIImageDeployment") ?? "dall-e-3";
        var key = Environment.GetEnvironmentVariable("AzureOpenAIKey");

        AzureKeyCredential credential = new AzureKeyCredential(key);


        // Authenticate with Azure OpenAI using DefaultAzureCredential (Managed Identity)
        _azureClient = new AzureOpenAIClient(new Uri(endpoint), credential);
        _azureImageClient = new AzureOpenAIClient(new Uri(imageEndpoint), credential);
        //var dalleClient = new OpenAIClient(new Uri(endpoint), credential);



        // Initialize ChatClient
        _chatClient = _azureClient.GetChatClient(deploymentName);
    }

    [Function("ProcessAIRequest")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req)
    {
        _logger.LogInformation("Processing OpenAI request...");

        // Read request body
        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        var requestData = JsonSerializer.Deserialize<Dictionary<string, string>>(requestBody);

        if (requestData == null || !requestData.ContainsKey("type") || !requestData.ContainsKey("message") )
        {
            return new BadRequestObjectResult("Invalid request: 'type' and 'message' are required.");
        }

        string type = requestData["type"];
        string userMessage = requestData["message"];
        string? documentContent = requestData.ContainsKey("document") ? requestData["document"] : null;

        // Prepare chat messages
        var messages = new List<ChatMessage>
        {
            new UserChatMessage("You are an AI assistant."),
            new UserChatMessage(userMessage)
        };

        if (req.HasFormContentType && req.Form.Files.Count > 0)
        {
            var file = req.Form.Files["file"];
            string requestType = req.Form["type"];


            if (file != null)
            {
                _logger.LogInformation($"Received file: {file.FileName}, Type: {requestType}");

                string extractedText = await ExtractTextFromFile(file);

                if (string.IsNullOrEmpty(extractedText))
                {
                    return new BadRequestObjectResult(new { response = "Unable to extract text from file." });
                }

                // Prepare messages for OpenAI
                messages = new List<ChatMessage>
                {
                    new UserChatMessage("You are an AI that processes documents."),
                    new UserChatMessage(extractedText)
                };
            }
        }

        if (type == "image")
        {
            
            string imageUrl = await GenerateImageWithDalle(userMessage);
            return new OkObjectResult(new { imageUrl = imageUrl });
            //await response.WriteAsJsonAsync(new { imageUrl });
        }

        // Chat Completion Options
        var options = new ChatCompletionOptions
        {
            Temperature = 0.7f,
            MaxOutputTokenCount = 1000,
            TopP = 0.95f,
            FrequencyPenalty = 0f,
            PresencePenalty = 0f
        };

        try
        {

            // Get AI response
            ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);
            string aiResponse = "";

            // Print the response
            if (completion != null)
            {

                aiResponse = JsonSerializer.Serialize(completion.Content[0].Text, new JsonSerializerOptions() { WriteIndented = true });
            }


            return new OkObjectResult(new { response = aiResponse });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error calling Azure OpenAI: {ex.Message}");
            return new StatusCodeResult(500);
        }
    }


    private async Task<string> GenerateImageWithDalle(string prompt)
    {
        try
        {
            var data = _azureImageClient.GetImageClient(_imageDeploymentName);

            ImageGenerationOptions imageGenerationOptions = new ImageGenerationOptions();
            // imageGenerationOptions.Size = 
            imageGenerationOptions.Size = GeneratedImageSize.W1024xH1024;
            imageGenerationOptions.Quality = GeneratedImageQuality.Standard;
            GeneratedImage generatedImage = await data.GenerateImageAsync(prompt, imageGenerationOptions);
            return generatedImage.ImageUri.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error calling Azure OpenAI: {ex.Message}");
            throw ex;
           // return new StatusCodeResult(500);
        }
        
        return string.Empty;
       
    }

    // Function to extract text based on file type
    private static async Task<string> ExtractTextFromFile(IFormFile file)
    {
        using (var stream = new MemoryStream())
        {
            await file.CopyToAsync(stream);
            stream.Position = 0;

            string extension = Path.GetExtension(file.FileName).ToLower();
            if (extension == ".pdf") return ExtractTextFromPdf(stream);
            if (extension == ".docx") return ExtractTextFromDocx(stream);
            if (extension == ".txt") return ExtractTextFromTxt(stream);
        }
        return string.Empty;
    }

    // Extract text from PDF
    private static string ExtractTextFromPdf(Stream stream)
    {
        StringBuilder text = new StringBuilder();
        using (PdfDocument pdf = PdfDocument.Open(stream))
        {
            foreach (var page in pdf.GetPages())
            {
                text.AppendLine(page.Text);
            }
        }
        return text.ToString();
    }

    // Extract text from DOCX
    private static string ExtractTextFromDocx(Stream stream)
    {
        StringBuilder text = new StringBuilder();
        using (WordprocessingDocument doc = WordprocessingDocument.Open(stream, false))
        {
            var body = doc.MainDocumentPart.Document.Body;
            text.Append(body.InnerText);
        }
        return text.ToString();
    }

    // Extract text from TXT
    private static string ExtractTextFromTxt(Stream stream)
    {
        using (StreamReader reader = new StreamReader(stream))
        {
            return reader.ReadToEnd();
        }
    }
}
