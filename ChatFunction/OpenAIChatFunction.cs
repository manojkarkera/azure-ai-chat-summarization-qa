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
using ChatFunction;
using DocumentFormat.OpenXml.EMMA;
using DocumentFormat.OpenXml.Packaging;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

        if (!req.HasFormContentType)
        {
            return await ProcessChatOrImageOrRagRequest(req);
        }

        return await ProcessDocumentRequest(req);
    }


    private async Task<IActionResult> ProcessRAGRequest(string message)
    {
        // Use Azure OpenAI to generate SQL query
        string sqlQuery = await ConvertToSQL(message);
        Console.WriteLine($"Generated SQL Query: {sqlQuery}");

        // Execute SQL Query
        SQLServerConnector db = new SQLServerConnector();
        string result = db.ExecuteQuery(sqlQuery);

        var options = new ChatCompletionOptions
        {
            Temperature = 0.7f,
            MaxOutputTokenCount = 1000,
            TopP = 0.95f,
            FrequencyPenalty = 0f,
            PresencePenalty = 0f
        };

        // Step 2: Send Query & Result to ChatGPT for a user-friendly message
        var messages = new List<ChatMessage>
        {
            new UserChatMessage("You are an AI assistant that formats financial responses in user-friendly language."),
            new UserChatMessage($"User asked: {message}"),
            new UserChatMessage($"Database result: {result}"),
            new UserChatMessage("Convert the result into a natural language response for the user."),
        };

        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);
        string aiResponse = completion?.Content[0].Text ?? "No response from AI.";

        return new OkObjectResult(new { response = aiResponse });

    }

    async Task<string> ConvertToSQL(string userQuery)
    {

        var options = new ChatCompletionOptions
        {
            Temperature = 0.7f,
            MaxOutputTokenCount = 1000,
            TopP = 0.95f,
            FrequencyPenalty = 0f,
            PresencePenalty = 0f
        };

        var messages = new List<ChatMessage>
        {
            new UserChatMessage( "You are an AI that converts natural language into SQL queries."),
            new UserChatMessage("Database Schema:"),
            new UserChatMessage("IncomeExpenses table: Id (int), Amount (decimal), Type (varchar), Date (datetime), CategoryId (int, foreign key to Categories.Id)"),
            new UserChatMessage("Categories table: Id (int), Name (varchar)"),
            new UserChatMessage("Ensure correct operator precedence by using parentheses in OR conditions."),
            new UserChatMessage($"Convert this into SQL: {userQuery}")
        };


        try
        {
            ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);
            string aiResponse = completion?.Content[0].Text ?? "No response from AI.";
            return aiResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error calling Azure OpenAI: {ex.Message}");
            return string.Empty;
        }
    }

    private async Task<IActionResult> ProcessChatOrImageOrRagRequest(HttpRequest req)
    {
        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        var requestData = JsonSerializer.Deserialize<Dictionary<string, string>>(requestBody);

        if (requestData == null || !requestData.ContainsKey("type") || !requestData.ContainsKey("message"))
        {
            return new BadRequestObjectResult("Invalid request: 'type' and 'message' are required.");
        }

        string type = requestData["type"];
        string userMessage = requestData["message"];

        return type switch
        {
            "image" => await ProcessImageRequest(userMessage),
            "rag" => await ProcessRAGRequest(userMessage),
            _ => await ProcessChatRequest(userMessage)
        };
    }

    private async Task<IActionResult> ProcessImageRequest(string prompt)
    {
        string imageUrl = await GenerateImageWithDalle(prompt);
        return new OkObjectResult(new { imageUrl });
    }

    private async Task<IActionResult> ProcessChatRequest(string userMessage)
    {
        var messages = new List<ChatMessage>
        {
            new UserChatMessage("You are an AI assistant."),
            new UserChatMessage(userMessage)
        };

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
            ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);
            string aiResponse = completion?.Content[0].Text ?? "No response from AI.";

            return new OkObjectResult(new { response = aiResponse });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error calling Azure OpenAI: {ex.Message}");
            return new StatusCodeResult(500);
        }
    }

    private async Task<IActionResult> ProcessDocumentRequest(HttpRequest req)
    {
        var form = await req.ReadFormAsync();
        var file = form.Files["file"];
        string requestType = form["type"];

        if (file == null)
        {
            return new BadRequestObjectResult("File is required.");
        }

        _logger.LogInformation($"Received file: {file.FileName}, Type: {requestType}");

        string extractedText = await ExtractTextFromFile(file);

        if (string.IsNullOrEmpty(extractedText))
        {
            return new BadRequestObjectResult(new { response = "Unable to extract text from file." });
        }

        var messages = new List<ChatMessage>
        {
            new UserChatMessage("You are an AI that processes documents."),
            new UserChatMessage(extractedText)
        };

        return await ProcessChatRequest(extractedText);
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

            return @"D:\chat_gpt_image.png";
            //throw ex;
            // return new StatusCodeResult(500);
        }
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


    //[Function("ProcessAIRequest")]
    //public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req)
    //{
    //    _logger.LogInformation("Processing OpenAI request...");
    //    var messages = new List<ChatMessage>();
    //    string type = string.Empty;
    //    string userMessage = string.Empty;
    //    if (!req.HasFormContentType)
    //    {
    //        // Read request body
    //        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
    //        var requestData = JsonSerializer.Deserialize<Dictionary<string, string>>(requestBody);

    //        if (requestData == null || !requestData.ContainsKey("type") || !requestData.ContainsKey("message"))
    //        {
    //            return new BadRequestObjectResult("Invalid request: 'type' and 'message' are required.");
    //        }

    //        type = requestData["type"];
    //        userMessage = requestData["message"];
    //        //string? documentContent = requestData.ContainsKey("document") ? requestData["document"] : null;

    //        // Prepare chat messages
    //        messages = new List<ChatMessage>
    //    {
    //        new UserChatMessage("You are an AI assistant."),
    //        new UserChatMessage(userMessage)
    //    };
    //    }


    //    if (req.HasFormContentType && req.Form.Files.Count > 0)
    //    {
    //        var form = await req.ReadFormAsync(); // 
    //        var file = form.Files["file"];
    //        string requestType = form["type"];


    //        if (file != null)
    //        {
    //            _logger.LogInformation($"Received file: {file.FileName}, Type: {requestType}");

    //            string extractedText = await ExtractTextFromFile(file);

    //            if (string.IsNullOrEmpty(extractedText))
    //            {
    //                return new BadRequestObjectResult(new { response = "Unable to extract text from file." });
    //            }

    //            // Prepare messages for OpenAI
    //            messages = new List<ChatMessage>
    //            {
    //                new UserChatMessage("You are an AI that processes documents."),
    //                new UserChatMessage(extractedText)
    //            };
    //        }
    //    }

    //    if (type == "image")
    //    {

    //        string imageUrl = await GenerateImageWithDalle(userMessage);
    //        return new OkObjectResult(new { imageUrl = imageUrl });
    //        //await response.WriteAsJsonAsync(new { imageUrl });
    //    }

    //    // Chat Completion Options
    //    var options = new ChatCompletionOptions
    //    {
    //        Temperature = 0.7f,
    //        MaxOutputTokenCount = 1000,
    //        TopP = 0.95f,
    //        FrequencyPenalty = 0f,
    //        PresencePenalty = 0f
    //    };

    //    try
    //    {

    //        // Get AI response
    //        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);
    //        string aiResponse = "";

    //        // Print the response
    //        if (completion != null)
    //        {

    //            aiResponse = completion.Content[0].Text;
    //        }


    //        return new OkObjectResult(new { response = aiResponse });
    //    }
    //    catch (Exception ex)
    //    {
    //        //var response = @"""Manoj.Python is a high-level programming language that is widely used for various applications, including artificial intelligence (AI). As an AI assistant, I can provide you with a brief explanation of Python and its relevance to AI.\n\nPython is known for its simplicity and readability, making it a popular choice among programmers. It has a vast standard library, which provides pre-built modules and functions that can be easily integrated into your code. This allows developers to save time and effort while building AI applications.\n\nPython's versatility and extensive libraries make it well-suited for AI development. One of the most popular AI libraries in Python is TensorFlow, which is used for machine learning and deep learning tasks. TensorFlow simplifies the process of creating and training neural networks, making it easier to build AI models.\n\nAnother widely used library is scikit-learn, which provides tools for data preprocessing, feature extraction, and model evaluation. It offers a wide range of machine learning algorithms and techniques, allowing developers to implement AI solutions more efficiently.\n\nPython's simplicity and clean syntax also make it an excellent choice for prototyping and experimentation. This is particularly advantageous for AI development, as it often involves iterative refinement and testing of different models and algorithms.\n\nMoreover, Python's large and active community ensures that there are numerous resources and tutorials available for AI development. This makes it easier for programmers to learn and get started with AI using Python.\n\nIn summary, Python is a powerful and flexible programming language that is widely used in AI development. Its simplicity, extensive libraries, and active community make it an excellent choice for building AI applications, from machine learning to deep learning and more.""";
    //        _logger.LogError($"Error calling Azure OpenAI: {ex.Message}");
    //        return new StatusCodeResult(500);
    //        //return new OkObjectResult(new { response = response });

    //    }
    //}

}
