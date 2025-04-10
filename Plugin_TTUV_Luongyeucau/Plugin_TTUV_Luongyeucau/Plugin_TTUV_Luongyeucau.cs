using Microsoft.Xrm.Sdk;
using System;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xrm.Sdk.Query;

namespace Plugin_TTUV_Luongyeucau
{
    public class Plugin_TTUV_Luongyeucau : IPlugin
    {
        ITracingService traceService;
        public bool check = false;
        public void Execute(IServiceProvider serviceProvider)
        {
            // Lấy các dịch vụ từ CRM
            IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            IOrganizationServiceFactory factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            IOrganizationService service = factory.CreateOrganizationService(context.UserId);
            traceService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            try
            {
                traceService.Trace("Bắt đầu thực thi plugin");
                if (context.Depth > 1)
                {
                    traceService.Trace("Plugin đã được kích hoạt lại (Depth > 1), bỏ qua việc thực thi.");
                    return;
                }
                if (context.Stage == 20 && context.MessageName == "Retrieve")
                {
                    if (context.InputParameters.Contains("ColumnSet"))
                    {
                        object rawColumnSet = context.InputParameters["ColumnSet"];
                        ColumnSet columnSet = rawColumnSet as ColumnSet;

                        if (columnSet != null)
                        {
                            columnSet.AddColumns("crdfd_mahoamucluongyeucau", "crdfd_idvmucluongyeucau");
                            traceService.Trace("Đã thêm các trường mã hóa vào ColumnSet trong Pre-operation.");
                        }
                    }

                    return; // Skip further processing in PreOp
                }

                if (context.MessageName == "Update" || context.MessageName == "Create")
                {
                    ProcessUpdateOrCreate(context, service);
                }
                else if (context.MessageName == "Retrieve" && context.Stage == 40)
                {
                    ProcessRetrieve(context, service);
                }
                else if (context.MessageName == "RetrieveMultiple")
                {
                    ProcessRetrieveMultiple(context, service);
                }
            }
            catch (Exception ex)
            {
                traceService.Trace($"Lỗi: {ex.StackTrace}");
                throw new InvalidPluginExecutionException($"Lỗi: {ex.Message}", ex);
            }
        }
        private void ProcessUpdateOrCreate(IPluginExecutionContext context, IOrganizationService service)
        {
            traceService.Trace("Xử lý hành động Update hoặc Create.");

            Entity target = (Entity)context.InputParameters["Target"];
            if (context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity)
            {
                if (target.LogicalName != "crdfd_thongtinphongvan")
                {
                    traceService.Trace("Bỏ qua plugin vì không phải thực thể crdfd_thongtinphongvan.");
                    return;
                }

                string apiUrl = "https://20.55.41.213:443/en_luong_ns_batch";
                var fieldsToEncrypt = new[]
                {
             new { Field = "crdfd_mucluongyeucau", EncryptedField = "crdfd_mahoamucluongyeucau", IvField = "crdfd_idvmucluongyeucau" }
            };

                var batchData = fieldsToEncrypt
    .Where(field => target.Contains(field.Field) && target[field.Field] != null)
    .Select(field => new BatchEncryptionRequest
    {
        FieldName = field.Field,
        salary = decimal.Parse(target[field.Field].ToString(), CultureInfo.InvariantCulture)
    })
    .ToList();


                if (!batchData.Any())
                {
                    traceService.Trace("Không có trường hợp hợp lệ để mã hóa.");
                    return;
                }

                try
                {
                    var encryptionResults = CallBatchEncryptionApi(apiUrl, batchData);
                    traceService.Trace("Dữ liệu gửi tới API mã hóa:");
                    foreach (var data in batchData)
                    {
                        traceService.Trace($"FieldName: {data.FieldName}, FieldValue: {data.salary}");
                    }
                    foreach (var attr in target.Attributes)
                    {
                        traceService.Trace($"Attribute: {attr.Key}, Value: {attr.Value}");
                    }

                    foreach (var result in encryptionResults)
                    {
                        var field = fieldsToEncrypt.FirstOrDefault(f => f.Field.Equals(result.FieldName, StringComparison.OrdinalIgnoreCase));
                        if (field != null)
                        {
                            target[field.EncryptedField] = result.EncodedSalary?.Trim();
                            target[field.IvField] = result.Iv?.Trim();
                            target[field.Field] = null; // Đặt giá trị gốc về null
                            traceService.Trace($"Cập nhật trường - EncryptedField: {field.EncryptedField}, IVField: {field.IvField}, Field : {field.Field}");
                        }
                    }
                    target["crdfd_checkplugin"] = true; // Gán giá trị tạm thời
                    service.Update(target);
                    traceService.Trace($"Update completed with ID: {target.Id}");
                }
                catch (Exception ex)
                {
                    traceService.Trace($"Error during encryption API call: {ex.Message}");
                    throw;
                }
            }
            else
            {
                traceService.Trace("Lỗi: Không tìm thấy Target trong InputParameters.");
            }
        }
        private void ProcessRetrieve(IPluginExecutionContext context, IOrganizationService service)
        {
            traceService.Trace("Xử lý hành động Retrieve.");
            if (context.OutputParameters.Contains("BusinessEntity") && context.OutputParameters["BusinessEntity"] is Entity)
            {
                var entity = (Entity)context.OutputParameters["BusinessEntity"];
                traceService.Trace($"Entity: {entity}");
                string currentUserEmail = GetCurrentUserEmail(service, context.UserId);
                var allowedEmails = new[] { "tinh.do@wecare-i.com", "xuan.pham@wecare.com.vn", "anh.le@wecare.com.vn", "khoi.tran@wecare.com.vn", "van.duong@wecare.com.vn" };

                if (!Array.Exists(allowedEmails, email => email.Equals(currentUserEmail, StringComparison.OrdinalIgnoreCase)))
                {
                    traceService.Trace("User không có quyền giải mã.");
                    return;
                }
                string apiUrl = "https://20.55.41.213:443/de_luong_ns_batch";
                var fieldsToDecrypt = new[]
                {
            new { EncryptedField = "crdfd_mahoamucluongyeucau", IvField = "crdfd_idvmucluongyeucau", ResultField = "crdfd_mucluongyeucau" }
        };

                // Tạo danh sách batch cho API giải mã
                var batchData = fieldsToDecrypt
                    .Where(field =>
                        entity.Contains(field.EncryptedField) &&
                        entity.Contains(field.IvField) &&
                        !string.IsNullOrEmpty(entity.GetAttributeValue<string>(field.EncryptedField)) &&
                        !string.IsNullOrEmpty(entity.GetAttributeValue<string>(field.IvField)))
                    .Select(field => new BatchDecryptionRequest
                    {
                        EntityId = entity.Id,
                        EncryptedValue = entity.GetAttributeValue<string>(field.EncryptedField)?.Trim(),
                        IV = entity.GetAttributeValue<string>(field.IvField)?.Trim(),
                        ResultField = field.ResultField
                    })
                    .ToList();
                traceService.Trace($"Giá trị mã hóa: {entity.GetAttributeValue<string>("crdfd_mahoamucluongyeucau")}");
                traceService.Trace($"Giá trị IV: {entity.GetAttributeValue<string>("crdfd_idvmucluongyeucau")}");
                traceService.Trace($"Entity chứa crdfd_mahoamucluongyeucau?: {entity.Contains("crdfd_mahoamucluongyeucau")}");
                traceService.Trace($"Entity chứa crdfd_idvmucluongyeucau?: {entity.Contains("crdfd_idvmucluongyeucau")}");

                if (!batchData.Any())
                {
                    traceService.Trace("Không có dữ liệu hợp lệ để giải mã.");
                    return;
                }

                try
                {
                    var decryptionResults = CallBatchDecryptionApi(apiUrl, batchData);

                    foreach (var field in fieldsToDecrypt)
                    {
                        if (!entity.Contains(field.EncryptedField) ||
                            string.IsNullOrEmpty(entity.GetAttributeValue<string>(field.EncryptedField)))
                        {
                            traceService.Trace($"Trường {field.ResultField} không có dữ liệu mã hóa, giữ nguyên giá trị hiện tại.");
                            continue;
                        }

                        if (entity.Contains(field.ResultField) && entity[field.ResultField] == null)
                        {
                            traceService.Trace($"Trường {field.ResultField} đã là null, giữ nguyên giá trị null.");
                            continue;
                        }

                        var result = decryptionResults.FirstOrDefault(r =>
                            r.EncodedSalary == entity.GetAttributeValue<string>(field.EncryptedField) &&
                            r.IV == entity.GetAttributeValue<string>(field.IvField));
                        decimal parsedSalary;
                        if (result != null && decimal.TryParse(result.DecodedSalary, out parsedSalary))
                        {
                            entity[field.ResultField] = parsedSalary;
                            traceService.Trace($"Đã giải mã và cập nhật trường {field.ResultField} với giá trị: {parsedSalary}");
                        }
                        else
                        {
                            traceService.Trace($"Không thể giải mã trường {field.ResultField} hoặc kết quả không hợp lệ.");
                        }
                    }

                }
                catch (Exception ex)
                {
                    traceService.Trace($"Lỗi khi giải mã dữ liệu: {ex.Message}");
                    traceService.Trace($"Stack Trace: {ex.StackTrace}");
                    throw;
                }
            }
            else
            {
                traceService.Trace("Lỗi: Không tìm thấy BusinessEntity hoặc kiểu không hợp lệ.");
            }
        }

        private void ProcessRetrieveMultiple(IPluginExecutionContext context, IOrganizationService service)
        {
            traceService.Trace("Xử lý hành động RetrieveMultiple.");
            var entityCollection = (EntityCollection)context.OutputParameters["BusinessEntityCollection"];
            string currentUserEmail = GetCurrentUserEmail(service, context.UserId);
            var allowedEmails = new[] { "tinh.do@wecare-i.com", "xuan.pham@wecare.com.vn", "anh.le@wecare.com.vn", "khoi.tran@wecare.com.vn", "van.duong@wecare.com.vn" };

            if (!Array.Exists(allowedEmails, email => email.Equals(currentUserEmail, StringComparison.OrdinalIgnoreCase)))
            {
                traceService.Trace("User không có quyền giải mã.");
                return;
            }

            if (context.OutputParameters.Contains("BusinessEntityCollection") && context.OutputParameters["BusinessEntityCollection"] is EntityCollection)
            {
                string apiUrl = "https://20.55.41.213:443/de_luong_ns_batch"; // API hỗ trợ batch
                var fieldsToDecrypt = new[]
                {
            new { EncryptedField = "crdfd_mahoamucluongyeucau", IvField = "crdfd_idvmucluongyeucau", ResultField = "crdfd_mucluongyeucau" }
        };

                var entities = entityCollection.Entities.ToList();
                const int chunkSize = 50;

                for (int i = 0; i < entities.Count; i += chunkSize)
                {
                    var chunk = entities.Skip(i).Take(chunkSize).ToList();
                    traceService.Trace($"Đang xử lý chunk từ bản ghi {i} đến {i + chunk.Count - 1}.");

                    var batchData = chunk.SelectMany(entity =>
                        fieldsToDecrypt.Where(field =>
                            entity.Contains(field.EncryptedField) &&
                            entity.Contains(field.IvField) &&
                            !string.IsNullOrEmpty(entity.GetAttributeValue<string>(field.EncryptedField)) &&
                            !string.IsNullOrEmpty(entity.GetAttributeValue<string>(field.IvField))
                        )
                        .Select(field => new BatchDecryptionRequest
                        {
                            EntityId = entity.Id,
                            EncryptedValue = entity.GetAttributeValue<string>(field.EncryptedField),
                            IV = entity.GetAttributeValue<string>(field.IvField),
                            ResultField = field.ResultField
                        })
                    ).ToList();

                    // Log các trường hợp dữ liệu không hợp lệ
                    var invalidData = chunk.Where(entity =>
                        fieldsToDecrypt.Any(field =>
                            !entity.Contains(field.EncryptedField) ||
                            !entity.Contains(field.IvField) ||
                            string.IsNullOrEmpty(entity.GetAttributeValue<string>(field.EncryptedField)) ||
                            string.IsNullOrEmpty(entity.GetAttributeValue<string>(field.IvField))
                        )
                    );

                    foreach (var invalidEntity in invalidData)
                    {
                        traceService.Trace($"Dữ liệu không hợp lệ cho EntityId={invalidEntity.Id}");
                    }

                    if (!batchData.Any())
                    {
                        traceService.Trace("Không có dữ liệu hợp lệ trong batchData.");
                        continue;
                    }

                    try
                    {
                        var decryptionResults = CallBatchDecryptionApi(apiUrl, batchData);

                        foreach (var result in decryptionResults)
                        {
                            var mapping = batchData.FirstOrDefault(b => b.EncryptedValue == result.EncodedSalary && b.IV == result.IV);
                            if (mapping != null)
                            {
                                var entity = chunk.FirstOrDefault(e => e.Id == mapping.EntityId);
                                if (entity != null)
                                {
                                    entity[mapping.ResultField] = decimal.Parse(result.DecodedSalary);
                                    traceService.Trace($"Đã giải mã và cập nhật trường {mapping.ResultField} cho bản ghi {entity.Id}.");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        traceService.Trace($"Lỗi khi gọi API batch: {ex.Message}");
                        throw;
                    }
                }
            }
        }


        private List<EncryptionResult> CallBatchEncryptionApi(string apiUrl, List<BatchEncryptionRequest> batchData)
        {
            try
            {
                System.Net.ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;
                using (HttpClient client = new HttpClient())
                {
                    string jsonPayload = JsonConvert.SerializeObject(batchData);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    traceService.Trace($"Payload JSON gửi tới API mã hóa: {jsonPayload}");
                    var response = client.PostAsync(apiUrl, content).Result;
                    if (response.IsSuccessStatusCode)
                    {
                        string responseContent = response.Content.ReadAsStringAsync().Result;
                        var results = JsonConvert.DeserializeObject<List<EncryptionResult>>(responseContent);

                        // Log từng kết quả mã hóa để kiểm tra
                        foreach (var result in results)
                        {
                            traceService.Trace($"Kết quả mã hóa từ API - FieldName: {result.FieldName}, EncodedSalary: {result.EncodedSalary}, IV: {result.Iv}");
                        }
                        return results;
                    }
                    else
                    {
                        traceService.Trace($"API mã hóa lỗi: {response.StatusCode}, Nội dung: {response.Content.ReadAsStringAsync().Result}");
                        throw new Exception($"API trả về lỗi với mã trạng thái: {response.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                traceService.Trace($"Lỗi khi gọi API batch: {ex.Message}");
                throw;
            }
        }
        // Phương thức gọi API batch
        private List<DecryptionResult> CallBatchDecryptionApi(string apiUrl, List<BatchDecryptionRequest> batchData)
        {
            try
            {
                System.Net.ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;
                using (HttpClient client = new HttpClient())
                {
                    // Serialize batchData thành JSON
                    string jsonPayload = JsonConvert.SerializeObject(batchData, Formatting.Indented);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    traceService.Trace($"Payload gửi tới API giải mã: {jsonPayload}");
                    var response = client.PostAsync(apiUrl, content).Result;

                    if (response.IsSuccessStatusCode)
                    {
                        string responseContent = response.Content.ReadAsStringAsync().Result;
                        traceService.Trace($"API Response: {responseContent}");
                        var results = JsonConvert.DeserializeObject<List<DecryptionResult>>(responseContent);

                        return results;
                    }
                    else
                    {
                        string errorContent = response.Content.ReadAsStringAsync().Result;
                        traceService.Trace($"API trả về lỗi: {response.StatusCode} - Nội dung: {errorContent}");
                        throw new Exception($"Decryption API failed with status: {response.StatusCode} - {errorContent}");
                    }
                }
            }
            catch (Exception ex)
            {
                traceService.Trace($"Lỗi khi gọi API giải mã batch: {ex.Message}");
                throw;
            }
        }

        private EncryptionResult CallEncryptionApi(string apiUrl, decimal salary)
        {
            try
            {
                System.Net.ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;
                using (HttpClient client = new HttpClient())
                {
                    var payload = new { salary };
                    string jsonPayload = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    var response = client.PostAsync(apiUrl, content).Result;
                    if (response.IsSuccessStatusCode)
                    {
                        string responseContent = response.Content.ReadAsStringAsync().Result;
                        return JsonConvert.DeserializeObject<EncryptionResult>(responseContent);
                    }
                    else
                    {
                        traceService.Trace($"Lỗi API: {response.StatusCode} - {response.ReasonPhrase}");
                        throw new Exception($"API trả về lỗi với mã trạng thái: {response.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                traceService.Trace($"Lỗi khi gọi API: {ex.Message}");
                throw;
            }
        }

        private DecryptionResult CallDecryptionApi(string apiUrl, string encodedSalary, string iv)
        {
            try
            {
                System.Net.ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;
                using (HttpClient client = new HttpClient())
                {
                    var payload = new { encoded_salary = encodedSalary.Trim(), iv = iv.Trim() };
                    string jsonPayload = JsonConvert.SerializeObject(payload, Formatting.None);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    traceService.Trace($"Calling Decryption API with EncodedSalary: {encodedSalary}, IV: {iv}");
                    var response = client.PostAsync(apiUrl, content).Result;

                    if (response.IsSuccessStatusCode)
                    {
                        string responseContent = response.Content.ReadAsStringAsync().Result;
                        traceService.Trace($"Decryption API Response: {responseContent}");
                        return JsonConvert.DeserializeObject<DecryptionResult>(responseContent);
                    }
                    else
                    {
                        traceService.Trace($"Decryption API Error: {response.StatusCode}, Response: {response.Content.ReadAsStringAsync().Result}");
                        throw new Exception($"Decryption API failed with status: {response.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                traceService.Trace($"Lỗi khi gọi API: {ex.Message}");
                throw;
            }
        }

        private string GetCurrentUserEmail(IOrganizationService service, Guid userId)
        {
            var query = new Microsoft.Xrm.Sdk.Query.QueryExpression("systemuser")
            {
                ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("internalemailaddress")
            };
            query.Criteria.AddCondition("systemuserid", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, userId);

            var result = service.RetrieveMultiple(query);
            if (result.Entities.Count > 0)
            {
                return result.Entities[0].GetAttributeValue<string>("internalemailaddress");
            }

            return null;
        }
        public class BatchEncryptionRequest
        {
            public string FieldName { get; set; }
            public decimal salary { get; set; }
        }

        // Thêm trường FieldName vào lớp EncryptionResult
        public class EncryptionResult
        {
            [JsonProperty("encoded_salary")]
            public string EncodedSalary { get; set; }

            [JsonProperty("iv")]
            public string Iv { get; set; }

            [JsonProperty("FieldName")]
            public string FieldName { get; set; }

            [JsonProperty("message")]
            public string Message { get; set; }
        }

        public class DecryptionResult
        {
            [JsonProperty("decoded_salary")]
            public string DecodedSalary { get; set; }

            [JsonProperty("message")]
            public string Message { get; set; }

            // Thêm hai thuộc tính này
            [JsonProperty("encoded_salary")]
            public string EncodedSalary { get; set; }
            [JsonProperty("iv")]
            public string IV { get; set; }
        }

        public class BatchDecryptionRequest
        {
            public Guid EntityId { get; set; }

            [JsonProperty("encoded_salary")]
            public string EncryptedValue { get; set; }
            [JsonProperty("iv")]
            public string IV { get; set; }
            public string ResultField { get; set; }
        }

    }
}
