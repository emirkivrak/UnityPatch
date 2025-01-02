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

/// <summary>
/// UnityPatch: Editor window that allows you to:
/// 1) List modified files in your Unity (Git) repo and selectively create a patch.
/// 2) Upload the resulting patch to an AWS S3 bucket.
/// 3) List existing patches in S3, download them, and apply locally.
/// 4) Delete patches directly from S3.
/// 
/// No third-party libraries are required; it uses:
/// - Native Git commands via Process.
/// - UnityWebRequest for AWS interactions with Signature V4.
/// 
/// Add this file to an Editor folder (e.g., Assets/UnityPatch/Editor).
/// </summary>
public class UnityPatch : EditorWindow
{
    // AWS S3 fields
    private string awsAccessKey = "";
    private string awsSecretKey = "";
    private string regionName   = "us-east-1";  // e.g. "us-east-1", "eu-west-1"
    private string bucketName   = "";
    private string serviceName  = "s3";         // Typically "s3"

    // Git fields
    // Default to the root of the Unity project (one level above /Assets)
    private string repoPath  = Path.GetFullPath(Application.dataPath + "/../");
    private string patchName = "MyPatch";

    // For listing/downloading S3 objects
    private List<string> s3PatchKeys    = new List<string>();
    private Vector2 s3ListScrollPos;

    // For selecting specific modified files
    private List<string> changedFiles          = new List<string>();
    private Dictionary<string, bool> fileToggles = new Dictionary<string, bool>();
    private Vector2 changedFilesScrollPos;

    // EditorPrefs keys
    private const string PREF_AWS_ACCESS_KEY = "PassNow_AWSAccessKey";
    private const string PREF_AWS_SECRET_KEY = "PassNow_AWSSecretKey";
    private const string PREF_REGION_NAME    = "PassNow_RegionName";
    private const string PREF_BUCKET_NAME    = "PassNow_BucketName";

    [MenuItem("Tools/Unity Patch")]
    public static void ShowWindow()
    {
        GetWindow<UnityPatch>("Unity Patch");
    }
    
    private void OnEnable()
    {
        // Load saved keys
        awsAccessKey = EditorPrefs.GetString(PREF_AWS_ACCESS_KEY, "");
        awsSecretKey = EditorPrefs.GetString(PREF_AWS_SECRET_KEY, "");
        regionName   = EditorPrefs.GetString(PREF_REGION_NAME, "us-east-1");
        bucketName   = EditorPrefs.GetString(PREF_BUCKET_NAME, "");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("AWS S3 Configuration", EditorStyles.boldLabel);
        awsAccessKey = EditorGUILayout.TextField("AWS Access Key", awsAccessKey);
        awsSecretKey = EditorGUILayout.TextField("AWS Secret Key", awsSecretKey);
        regionName   = EditorGUILayout.TextField("Region (e.g. us-east-1)", regionName);
        bucketName   = EditorGUILayout.TextField("Bucket Name", bucketName);
        
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Save Keys", GUILayout.Width(120)))
        {
            SaveKeys();
        }
        if (GUILayout.Button("Clear Keys", GUILayout.Width(120)))
        {
            ClearKeys();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Git Configuration", EditorStyles.boldLabel);
        repoPath  = EditorGUILayout.TextField("Repo Path", repoPath);
        patchName = EditorGUILayout.TextField("Patch Name", patchName);

        EditorGUILayout.Space();

        // --------------------------------------------------------------------
        // File Selection Section
        // --------------------------------------------------------------------
        EditorGUILayout.LabelField("Select Modified Files", EditorStyles.boldLabel);
        if (GUILayout.Button("Refresh Changed Files"))
        {
            RefreshChangedFiles();
        }

        changedFilesScrollPos = EditorGUILayout.BeginScrollView(changedFilesScrollPos, GUILayout.Height(150));
        foreach (var file in changedFiles)
        {
            // Toggle each file
            bool currentValue = fileToggles.ContainsKey(file) && fileToggles[file];
            bool newValue = EditorGUILayout.ToggleLeft(file, currentValue);
            if (newValue != currentValue)
            {
                fileToggles[file] = newValue;
            }
        }
        EditorGUILayout.EndScrollView();

        // Create & Upload Patch (only selected files)
        if (GUILayout.Button("Create + Upload Patch to S3"))
        {
            List<string> selectedFiles = new List<string>();
            foreach (var kvp in fileToggles)
            {
                if (kvp.Value)
                {
                    selectedFiles.Add(kvp.Key);
                }
            }

            string patchFilePath = CreatePatch(patchName, repoPath, selectedFiles);
            if (!string.IsNullOrEmpty(patchFilePath))
            {
                UploadPatchToS3(patchFilePath);
            }
        }

        // --------------------------------------------------------------------
        // S3 Patch Listing Section
        // --------------------------------------------------------------------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("S3 Patch Management", EditorStyles.boldLabel);

        if (GUILayout.Button("List Patches in S3"))
        {
            s3PatchKeys.Clear();
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

        if (s3PatchKeys.Count > 0)
        {
            EditorGUILayout.LabelField("Available Patches in S3:", EditorStyles.boldLabel);
            s3ListScrollPos = EditorGUILayout.BeginScrollView(s3ListScrollPos, GUILayout.Height(130));
            foreach (var key in s3PatchKeys)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(key, GUILayout.Width(position.width - 180));

                // DOWNLOAD & APPLY
                if (GUILayout.Button("Download & Apply", GUILayout.Width(120)))
                {
                    string localPath = Path.Combine(repoPath, key);
                    DownloadPatchFromS3(key, localPath, (downloadSuccess, downloadError) =>
                    {
                        if (downloadSuccess)
                        {
                            ApplyPatch(localPath, repoPath);
                        }
                        else
                        {
                            UnityEngine.Debug.LogError("Download failed: " + downloadError);
                        }
                    });
                }

                // DELETE PATCH
                if (GUILayout.Button("Delete", GUILayout.Width(60)))
                {
                    DeletePatchFromS3(key);
                }

                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }

    #region (A) Refresh Changed Files
    private void RefreshChangedFiles()
    {
        changedFiles = GetModifiedFiles(repoPath);
        fileToggles.Clear();
        foreach (var file in changedFiles)
        {
            // Default toggle state to false
            fileToggles[file] = false;
        }
    }

    /// <summary>
    /// Get a list of modified files using "git status --porcelain"
    /// </summary>
    private List<string> GetModifiedFiles(string workingDirectory)
    {
        List<string> modifiedFiles = new List<string>();

        var processInfo = new ProcessStartInfo("git", "status --porcelain")
        {
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using (var process = Process.Start(processInfo))
        {
            process.WaitForExit();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            if (!string.IsNullOrEmpty(error))
            {
                UnityEngine.Debug.LogError("Git status error: " + error);
                return modifiedFiles;
            }

            // Example lines: "M  Assets/Scripts/Player.cs"
            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                // Skip lines too short or unrecognized
                if (line.Length <= 3) continue;
                string filePath = line.Substring(3).Trim();
                modifiedFiles.Add(filePath);
            }
        }

        return modifiedFiles;
    }
    #endregion

    #region (B) Create Patch with Selected Files
    private string CreatePatch(string patchFileName, string workingDirectory, List<string> selectedFiles)
    {
        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            UnityEngine.Debug.LogError("Invalid Git repo path.");
            return null;
        }

        if (selectedFiles == null || selectedFiles.Count == 0)
        {
            UnityEngine.Debug.LogWarning("No files selected for patch.");
            return null;
        }

        // Construct patch path
        string patchPath = Path.Combine(workingDirectory, patchFileName + ".patch");

        // Build file list for Git command
        // e.g., "file1.cs file2.txt"
        StringBuilder fileArgs = new StringBuilder();
        foreach (var f in selectedFiles)
        {
            // Wrap in quotes for paths with spaces
            fileArgs.Append($" \"{f}\"");
        }

        // e.g., git diff HEAD --output="MyPatch.patch" "Assets/Scripts/Player.cs" ...
        string gitCommand = $"diff HEAD --output=\"{patchPath}\" {fileArgs}";

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

    #region (C) AWS S3 Interactions
    // Upload, List, Download, Delete

    /// <summary>
    /// Upload a patch file to S3 using UnityWebRequest (Signature V4).
    /// </summary>
    private void UploadPatchToS3(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            UnityEngine.Debug.LogError("Patch file not found.");
            return;
        }

        string fileName = Path.GetFileName(filePath);
        byte[] fileData = File.ReadAllBytes(filePath);

        string host = $"{bucketName}.s3.{regionName}.amazonaws.com";
        string uri  = $"https://{host}/{fileName}";
        string method = "PUT";

        Dictionary<string, string> headers = new Dictionary<string, string>();
        PopulateAWSSignature(headers, method, fileData, host, $"/{fileName}");

        UnityWebRequest request = new UnityWebRequest(uri, method);
        request.uploadHandler   = new UploadHandlerRaw(fileData);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/octet-stream");

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
                UnityEngine.Debug.LogError($"Upload error: {request.error}");
            }
            else
            {
                UnityEngine.Debug.Log($"File '{fileName}' uploaded to S3 successfully!");
            }

            request.Dispose();
        };
    }

    /// <summary>
    /// List patch files in S3 (objects in the bucket).
    /// </summary>
    private void ListObjectsFromS3(Action<bool, List<string>, string> callback)
    {
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

    /// <summary>
    /// Download a patch from S3, then run callback upon completion.
    /// </summary>
    private void DownloadPatchFromS3(string key, string localPath, Action<bool, string> callback)
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

    /// <summary>
    /// Delete a patch object from S3 by key name.
    /// </summary>
    private void DeletePatchFromS3(string key)
    {
        string host   = $"{bucketName}.s3.{regionName}.amazonaws.com";
        string uri    = $"https://{host}/{key}";
        string method = "DELETE";

        Dictionary<string, string> headers = new Dictionary<string, string>();
        PopulateAWSSignature(headers, method, null, host, $"/{key}");

        UnityWebRequest request = new UnityWebRequest(uri, method);
        request.downloadHandler = new DownloadHandlerBuffer(); // no body needed for DELETE

        // Add the AWS signature headers
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
                UnityEngine.Debug.LogError($"Delete error: {request.error}");
            }
            else
            {
                UnityEngine.Debug.Log($"Successfully deleted patch '{key}' from S3!");
                // Optionally refresh the S3 list
                s3PatchKeys.Remove(key);
            }
            request.Dispose();
        };
    }

    #endregion

    #region (D) Apply Patch Locally
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

        // e.g., git apply "MyPatch.patch"
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

    #region (E) AWS Signature V4 Helper

    /// <summary>
    /// Adds AWS Signature Version 4 headers to the 'headers' dictionary.
    /// </summary>
    private void PopulateAWSSignature(Dictionary<string, string> headers, string method, byte[] body, string host, string canonicalUri)
    {
        string region  = regionName;
        string service = serviceName; // "s3"
        DateTime now   = DateTime.UtcNow;

        string amzDate   = now.ToString("yyyyMMddTHHmmssZ");
        string dateStamp = now.ToString("yyyyMMdd"); // For credential scope

        headers["host"] = host;
        headers["x-amz-date"] = amzDate;
        headers["x-amz-content-sha256"] = HashSHA256(body ?? new byte[0]);

        // Create canonical request
        string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        string canonicalHeaders =
            $"host:{host}\n" +
            $"x-amz-content-sha256:{headers["x-amz-content-sha256"]}\n" +
            $"x-amz-date:{amzDate}\n";

        // If there is a query string, separate it from the path
        string canonicalQueryString = "";
        if (canonicalUri.Contains("?"))
        {
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
        string algorithm       = "AWS4-HMAC-SHA256";
        string credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        string stringToSign =
            $"{algorithm}\n" +
            $"{amzDate}\n" +
            $"{credentialScope}\n" +
            hashedCanonicalRequest;

        // Calculate the signature
        byte[] signingKey  = GetSignatureKey(awsSecretKey, dateStamp, region, service);
        byte[] signature   = HmacSHA256(Encoding.UTF8.GetBytes(stringToSign), signingKey);
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
        byte[] kDate    = HmacSHA256(Encoding.UTF8.GetBytes(dateStamp), Encoding.UTF8.GetBytes("AWS4" + key));
        byte[] kRegion  = HmacSHA256(Encoding.UTF8.GetBytes(regionName), kDate);
        byte[] kService = HmacSHA256(Encoding.UTF8.GetBytes(serviceName), kRegion);
        byte[] kSigning = HmacSHA256(Encoding.UTF8.GetBytes("aws4_request"), kService);
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

    #region (F) EditorPrefs Storage
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
