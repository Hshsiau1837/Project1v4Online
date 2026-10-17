using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Ghi log mạng ra CSV (mỗi tiến trình một file) để làm số liệu E0 và báo cáo. Gắn vào NetworkManager.
/// Cột: time_s,type,role,clientId,rttMs,fps,detail
///   type = sample (mỗi interval giây) hoặc event (kết nối / ngắt kết nối / bắt đầu / dừng).
/// File nằm ở Application.persistentDataPath (đường dẫn được in ra Console khi bắt đầu).
/// </summary>
public class NetStatsLogger : MonoBehaviour
{
    [SerializeField] float intervalSeconds = 1f;

    NetworkManager nm;
    StreamWriter writer;
    float startTime, sampleTimer;
    int frames;
    bool wasListening;

    void Start()
    {
        nm = NetworkManager.Singleton;
        if (nm == null) { UnityEngine.Debug.LogError("NetStatsLogger: không thấy NetworkManager"); enabled = false; return; }

        string name = $"netlog_{DateTime.Now:yyyyMMdd_HHmmss}_p{Process.GetCurrentProcess().Id}.csv";
        string path = Path.Combine(Application.persistentDataPath, name);
        writer = new StreamWriter(path, false) { AutoFlush = true };
        writer.WriteLine("time_s,type,role,clientId,rttMs,fps,detail");
        UnityEngine.Debug.Log("NetStatsLogger ghi vào: " + path);
        startTime = Time.realtimeSinceStartup;

        nm.OnClientConnectedCallback += OnConnected;
        nm.OnClientDisconnectCallback += OnDisconnected;
    }

    void OnDestroy()
    {
        if (nm != null)
        {
            nm.OnClientConnectedCallback -= OnConnected;
            nm.OnClientDisconnectCallback -= OnDisconnected;
        }
        writer?.Dispose();
    }

    void Update()
    {
        if (nm == null) return;

        if (nm.IsListening != wasListening)
        {
            wasListening = nm.IsListening;
            Write("event", nm.IsListening ? "start" : "stop", Role(), nm.LocalClientId, "", "");
        }
        if (!nm.IsListening) return;

        frames++;
        sampleTimer += Time.unscaledDeltaTime;
        if (sampleTimer < intervalSeconds) return;

        float fps = frames / sampleTimer;
        frames = 0; sampleTimer = 0f;

        if (nm.IsServer)
        {
            bool any = false;
            foreach (ulong id in nm.ConnectedClientsIds)
            {
                if (id == NetworkManager.ServerClientId) continue;
                any = true;
                ulong rtt = nm.NetworkConfig.NetworkTransport.GetCurrentRtt(id);
                Write("sample", "", Role(), id, rtt.ToString(), fps.ToString("F1", CultureInfo.InvariantCulture));
            }
            if (!any) Write("sample", "", Role(), nm.LocalClientId, "0", fps.ToString("F1", CultureInfo.InvariantCulture));
        }
        else if (nm.IsConnectedClient)
        {
            ulong rtt = nm.NetworkConfig.NetworkTransport.GetCurrentRtt(NetworkManager.ServerClientId);
            Write("sample", "", Role(), nm.LocalClientId, rtt.ToString(), fps.ToString("F1", CultureInfo.InvariantCulture));
        }
    }

    void OnConnected(ulong id) => Write("event", "connected", Role(), id, "", "");
    void OnDisconnected(ulong id) => Write("event", "disconnected", Role(), id, "", "");

    string Role() => nm.IsHost ? "Host" : nm.IsServer ? "Server" : "Client";

    void Write(string type, string detail, string role, ulong clientId, string rtt, string fps)
    {
        if (writer == null) return;
        float t = Time.realtimeSinceStartup - startTime;
        writer.WriteLine($"{t.ToString("F2", CultureInfo.InvariantCulture)},{type},{role},{clientId},{rtt},{fps},{detail}");
    }
}