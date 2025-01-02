#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text;
using System;
using System.Net;
using System.Security.Cryptography;
using UnityEngine.Networking;

// Single-file example showing how to:
// 1) Create a Git patch
// 2) Upload it to AWS S3 (Signature V4) using UnityWebRequest
// 3) List existing patches in S3
// 4) Download a selected patch
// 5) Apply the patch to your local repo

public class UnityPatch : EditorWindow
{
    // AWS S3 fields
    private string awsAccessKey = "";
    private string awsSecretKey = "";
    private string regionName = "us-east-1";  // e.g., "us-east-1", "eu-west-1"
    private string bucketName = "";
    private string serviceName = "s3"; // Typically "s3"

    // Git fields
    private string repoPath = Path.GetFullPath(Application.dataPath + "/../"); 
    private string patchName = "MyPatch";

    // For listing/downloading S3 objects
    private List<string> s3PatchKeys = new List<string>();
    private Vector2 scrollPos;
    
    private const string PREF_AWS_ACCESS_KEY = "PassNow_AWSAccessKey";
    private const string PREF_AWS_SECRET_KEY = "PassNow_AWSSecretKey";
    private const string PREF_REGION_NAME = "PassNow_RegionName";
    private const string PREF_BUCKET_NAME = "PassNow_BucketName";

    [MenuItem("Tools/PassNow")]
    public static void ShowWindow()
    {
        GetWindow<UnityPatch>("Patch Manager");
    }
    
    private void OnEnable()
    {
        // Load saved keys
        awsAccessKey = EditorPrefs.GetString(PREF_AWS_ACCESS_KEY, "");
        awsSecretKey = EditorPrefs.GetString(PREF_AWS_SECRET_KEY, "");
        regionName = EditorPrefs.GetString(PREF_REGION_NAME, "us-east-1");
        bucketName = EditorPrefs.GetString(PREF_BUCKET_NAME, "");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("AWS S3 Configuration", EditorStyles.boldLabel);
        awsAccessKey = EditorGUILayout.TextField("AWS Access Key", awsAccessKey);
        awsSecretKey = EditorGUILayout.TextField("AWS Secret Key", awsSecretKey);
        regionName   = EditorGUILayout.TextField("Region (e.g. us-east-1)", regionName);
        bucketName   = EditorGUILayout.TextField("Bucket Name", bucketName);
        
        if (GUILayout.Button("Save Keys"))
        {
            SaveKeys();
        }
        
        if (GUILayout.Button("Clear Keys"))
        {
            ClearKeys();
        }
        
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Git Configuration", EditorStyles.boldLabel);
        repoPath  = EditorGUILayout.TextField("Repo Path", repoPath);
        patchName = EditorGUILayout.TextField("Patch Name", patchName);

        EditorGUILayout.Space();

        // CREATE & UPLOAD PATCH
        if (GUILayout.Button("Create + Upload Patch to S3"))
        {
            string patchFilePath = CreatePatch(patchName, repoPath);
            if (!string.IsNullOrEmpty(patchFilePath))
            {
                // Upload to S3
                UploadPatchToS3(patchFilePath);
            }
        }

        // LIST PATCHES
        if (GUILayout.Button("List Patches in S3"))
        {
            s3PatchKeys.Clear();
            // Attempt listing
            ListObjectsFromS3((success, objectKeys, error) =>
            {
                if (success && objectKeys != null)
                {
                    s3PatchKeys.AddRange(objectKeys);
                    UnityEngine.Debug.Log("Listed patches from S3.");
                }
                else
                {
                    UnityEngine.Debug.LogError("Failed to list patches: " + error);
                }
            });
        }

        // Display S3 object keys
        if (s3PatchKeys.Count > 0)
        {
            EditorGUILayout.LabelField("Available Patches in S3:", EditorStyles.boldLabel);
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.Height(100));
            foreach (var key in s3PatchKeys)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(key);

                // DOWNLOAD button
                if (GUILayout.Button("Download & Apply", GUILayout.Width(150)))
                {
                    string localPath = Path.Combine(repoPath, key);
                    DownloadPatchFromS3(key, localPath, (downloadSuccess, downloadError) =>
                    {
                        if (downloadSuccess)
                        {
                            // Apply patch
                            ApplyPatch(localPath, repoPath);
                        }
                        else
                        {
                            UnityEngine.Debug.LogError("Download failed: " + downloadError);
                        }
                    });
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }

    #region 1) Create Patch (Git)

    private string CreatePatch(string patchFileName, string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            UnityEngine.Debug.LogError("Invalid Git repo path.");
            return null;
        }

        // Construct patch path
        string patchPath = Path.Combine(workingDirectory, patchFileName + ".patch");

        // Example: git diff HEAD --output="MyPatch.patch"
        string gitCommand = $"diff HEAD --output=\"{patchPath}\"";

        ProcessStartInfo psi = new ProcessStartInfo("git", gitCommand)
        {
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using (Process gitProcess = Process.Start(psi))
            {
                gitProcess.WaitForExit();

                string output = gitProcess.StandardOutput.ReadToEnd();
                string error  = gitProcess.StandardError.ReadToEnd();

                if (!string.IsNullOrEmpty(error))
                {
                    UnityEngine.Debug.LogError("Git Error: " + error);
                    return null;
                }
                else
                {
                    UnityEngine.Debug.Log($"Patch created at: {patchPath}");
                    return patchPath;
                }
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"Failed to create patch: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region 2) Upload Patch to AWS S3 (Using Signature V4 with UnityWebRequest)

    private void UploadPatchToS3(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            UnityEngine.Debug.LogError("Patch file not found.");
            return;
        }

        // We'll perform a PUT object request to S3: "PUT /{key}"
        // The host is typically "{bucketName}.s3.{region}.amazonaws.com"
        string fileName = Path.GetFileName(filePath);
        byte[] fileData = File.ReadAllBytes(filePath);

        string host = $"{bucketName}.s3.{regionName}.amazonaws.com";
        string uri  = $"https://{host}/{fileName}";
        string method = "PUT";

        Dictionary<string, string> headers = new Dictionary<string, string>();
        // Populate the necessary AWS Signature V4 headers
        PopulateAWSSignature(headers, method, fileData, host, $"/{fileName}");

        // Start the upload
        UnityWebRequest request = new UnityWebRequest(uri, method);
        request.uploadHandler   = new UploadHandlerRaw(fileData);
        request.downloadHandler = new DownloadHandlerBuffer();

        // Add headers to request
        foreach (var kvp in headers)
            request.SetRequestHeader(kvp.Key, kvp.Value);

        // For a PUT, set content-type as needed (e.g. application/octet-stream)
        request.SetRequestHeader("Content-Type", "application/octet-stream");

        // Send request
        var operation = request.SendWebRequest();
        operation.completed += (op) =>
        {
#if UNITY_2020_2_OR_NEWER
            if (request.result != UnityWebRequest.Result.Success)
#else
            if (request.isNetworkError || request.isHttpError)
#endif
            {
                UnityEngine.Debug.LogError($"Upload error: {request.error}");
            }
            else
            {
                UnityEngine.Debug.Log($"File '{fileName}' uploaded to S3 successfully!");
            }

            request.Dispose();
        };
    }

    #endregion

    #region 3) List Objects in AWS S3 (Using UnityWebRequest + Signature V4)

    private void ListObjectsFromS3(System.Action<bool, List<string>, string> callback)
    {
        // GET request to: "https://{bucket}.s3.{region}.amazonaws.com?list-type=2"
        // We'll parse the resulting XML for object keys

        string host = $"{bucketName}.s3.{regionName}.amazonaws.com";
        string path = "/?list-type=2";
        string uri  = $"https://{host}{path}";
        string method = "GET";

        Dictionary<string, string> headers = new Dictionary<string, string>();
        PopulateAWSSignature(headers, method, null, host, path);

        UnityWebRequest request = UnityWebRequest.Get(uri);
        foreach (var kvp in headers)
            request.SetRequestHeader(kvp.Key, kvp.Value);

        var operation = request.SendWebRequest();
        operation.completed += (op) =>
        {
#if UNITY_2020_2_OR_NEWER
            if (request.result != UnityWebRequest.Result.Success)
#else
            if (request.isNetworkError || request.isHttpError)
#endif
            {
                callback(false, null, request.error);
            }
            else
            {
                // Parse the XML response to find <Key>...</Key> entries
                string responseText = request.downloadHandler.text;
                List<string> keys = ParseKeysFromListBucketResult(responseText);
                callback(true, keys, null);
            }

            request.Dispose();
        };
    }

    private List<string> ParseKeysFromListBucketResult(string xmlText)
    {
        // Very naive parser for demonstration; a real XML parser is recommended
        List<string> keys = new List<string>();
        const string startTag = "<Key>";
        const string endTag   = "</Key>";

        int currentIndex = 0;
        while (true)
        {
            int startIndex = xmlText.IndexOf(startTag, currentIndex, StringComparison.Ordinal);
            if (startIndex < 0) break;

            int endIndex = xmlText.IndexOf(endTag, startIndex, StringComparison.Ordinal);
            if (endIndex < 0) break;

            int contentStart = startIndex + startTag.Length;
            string key = xmlText.Substring(contentStart, endIndex - contentStart);
            keys.Add(key);

            currentIndex = endIndex + endTag.Length;
        }
        return keys;
    }

    #endregion

    #region 4) Download Patch from S3 (Using UnityWebRequest + Signature V4)

    private void DownloadPatchFromS3(string key, string localPath, System.Action<bool, string> callback)
    {
        string host = $"{bucketName}.s3.{regionName}.amazonaws.com";
        string uri  = $"https://{host}/{key}";
        string method = "GET";

        Dictionary<string, string> headers = new Dictionary<string, string>();
        PopulateAWSSignature(headers, method, null, host, $"/{key}");

        UnityWebRequest request = UnityWebRequest.Get(uri);
        foreach (var kvp in headers)
            request.SetRequestHeader(kvp.Key, kvp.Value);

        var operation = request.SendWebRequest();
        operation.completed += (op) =>
        {
#if UNITY_2020_2_OR_NEWER
            if (request.result != UnityWebRequest.Result.Success)
#else
            if (request.isNetworkError || request.isHttpError)
#endif
            {
                callback(false, request.error);
            }
            else
            {
                try
                {
                    File.WriteAllBytes(localPath, request.downloadHandler.data);
                    UnityEngine.Debug.Log($"Patch downloaded to: {localPath}");
                    callback(true, null);
                }
                catch (Exception ex)
                {
                    callback(false, ex.Message);
                }
            }

            request.Dispose();
        };
    }

    #endregion

    #region 5) Apply Patch (Git)

    private void ApplyPatch(string patchFilePath, string workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            UnityEngine.Debug.LogError("Invalid Git repo path.");
            return;
        }
        if (string.IsNullOrEmpty(patchFilePath) || !File.Exists(patchFilePath))
        {
            UnityEngine.Debug.LogError("Patch file not found.");
            return;
        }

        // Example: git apply "MyPatch.patch"
        string gitCommand = $"apply \"{patchFilePath}\"";

        ProcessStartInfo psi = new ProcessStartInfo("git", gitCommand)
        {
            WorkingDirectory = workingDirectory,
            CreateNoWindow   = true,
            UseShellExecute  = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        try
        {
            using (Process gitProcess = Process.Start(psi))
            {
                gitProcess.WaitForExit();

                string output = gitProcess.StandardOutput.ReadToEnd();
                string error  = gitProcess.StandardError.ReadToEnd();

                if (!string.IsNullOrEmpty(error))
                {
                    UnityEngine.Debug.LogError("Git Apply Error: " + error);
                }
                else
                {
                    UnityEngine.Debug.Log("Patch applied successfully!");
                }
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"Failed to apply patch: {ex.Message}");
        }
    }

    #endregion

    #region AWS Signature V4 Helper

    /// <summary>
    /// Populates a dictionary with the necessary AWS Signature Version 4 headers.
    /// - This example uses your Access Key and Secret Key directly (not recommended for production).
    /// - For large files or advanced usage, consider chunked uploading and robust error handling.
    /// </summary>
    /// <param name="headers">Dictionary to fill with headers (including Authorization)</param>
    /// <param name="method">HTTP method (e.g., PUT, GET)</param>
    /// <param name="body">Request body byte array (null if GET, non-null if PUT)</param>
    /// <param name="host">Hostname (e.g., bucket.s3.us-east-1.amazonaws.com)</param>
    /// <param name="canonicalUri">The path part of the URL (e.g., "/myfile.txt" or "/?list-type=2")</param>
    private void PopulateAWSSignature(Dictionary<string, string> headers, string method, byte[] body, string host, string canonicalUri)
    {
        // Basic setup
        string region     = regionName;
        string service    = serviceName; // "s3"
        DateTime now      = DateTime.UtcNow;
        string amzDate    = now.ToString("yyyyMMddTHHmmssZ");
        string dateStamp  = now.ToString("yyyyMMdd"); // For credential scope

        headers["host"]          = host;
        headers["x-amz-date"]    = amzDate;
        headers["x-amz-content-sha256"] = HashSHA256(body ?? new byte[0]);

        // Create canonical request
        string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        string canonicalHeaders = 
            $"host:{host}\n" +
            $"x-amz-content-sha256:{headers["x-amz-content-sha256"]}\n" +
            $"x-amz-date:{amzDate}\n";

        string canonicalQueryString = ""; // For listing objects, we included "?list-type=2" in the URI, 
                                          // but for signing you put that into the canonical query if not in path
        if (canonicalUri.Contains("?"))
        {
            // Very naive approach: separate path from query
            var parts = canonicalUri.Split('?');
            canonicalUri      = parts[0];
            canonicalQueryString = parts.Length > 1 ? parts[1] : "";
        }

        string canonicalRequest = 
            $"{method}\n" + 
            $"{canonicalUri}\n" +
            $"{canonicalQueryString}\n" +
            $"{canonicalHeaders}\n" +
            $"{signedHeaders}\n" +
            $"{headers["x-amz-content-sha256"]}";

        string hashedCanonicalRequest = HashSHA256(Encoding.UTF8.GetBytes(canonicalRequest));

        // Create string to sign
        string algorithm = "AWS4-HMAC-SHA256";
        string credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        string stringToSign =
            $"{algorithm}\n" +
            $"{amzDate}\n" +
            $"{credentialScope}\n" +
            hashedCanonicalRequest;

        // Calculate the signature
        byte[] signingKey = GetSignatureKey(awsSecretKey, dateStamp, region, service);
        byte[] signature  = HmacSHA256(Encoding.UTF8.GetBytes(stringToSign), signingKey);
        string signatureHex = ToHexString(signature);

        // Create Authorization header
        string authorization = 
            $"{algorithm} Credential={awsAccessKey}/{credentialScope}, " +
            $"SignedHeaders={signedHeaders}, Signature={signatureHex}";

        headers["Authorization"] = authorization;
    }

    private string HashSHA256(byte[] data)
    {
        if (data == null) data = new byte[0];
        using (SHA256 sha256 = SHA256.Create())
        {
            return ToHexString(sha256.ComputeHash(data));
        }
    }

    private static byte[] HmacSHA256(byte[] data, byte[] key)
    {
        using (var hmac = new HMACSHA256(key))
        {
            return hmac.ComputeHash(data);
        }
    }

    // Returns the signing key to sign the request, per AWS docs
    private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
    {
        byte[] kDate    = HmacSHA256(System.Text.Encoding.UTF8.GetBytes(dateStamp),  System.Text.Encoding.UTF8.GetBytes("AWS4" + key));
        byte[] kRegion  = HmacSHA256(System.Text.Encoding.UTF8.GetBytes(regionName), kDate);
        byte[] kService = HmacSHA256(System.Text.Encoding.UTF8.GetBytes(serviceName), kRegion);
        byte[] kSigning = HmacSHA256(System.Text.Encoding.UTF8.GetBytes("aws4_request"), kService);
        return kSigning;
    }

    private static string ToHexString(byte[] bytes)
    {
        StringBuilder sb = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
            sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    #endregion

    #region EditorPrefs

    private void SaveKeys()
    {
        EditorPrefs.SetString(PREF_AWS_ACCESS_KEY, awsAccessKey);
        EditorPrefs.SetString(PREF_AWS_SECRET_KEY, awsSecretKey);
        EditorPrefs.SetString(PREF_REGION_NAME, regionName);
        EditorPrefs.SetString(PREF_BUCKET_NAME, bucketName);
        UnityEngine.Debug.Log("AWS keys saved successfully!");
    }

    private void ClearKeys()
    {
        EditorPrefs.DeleteKey(PREF_AWS_ACCESS_KEY);
        EditorPrefs.DeleteKey(PREF_AWS_SECRET_KEY);
        EditorPrefs.DeleteKey(PREF_REGION_NAME);
        EditorPrefs.DeleteKey(PREF_BUCKET_NAME);
        UnityEngine.Debug.Log("AWS keys cleared.");
    }
    
    #endregion
}
#endif