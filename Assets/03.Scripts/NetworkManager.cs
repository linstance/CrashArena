using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

public class NetworkManager : MonoBehaviour
{
    public static NetworkManager Instance { get; private set; }

    
    public bool useMockServer = true;

   
    public float baseSpeed = 50f;
    public float drag = 4f;

    
    public float dashImpulseForce = 30f;
    public float dashDuration = 0.2f;
    public float dashCooldown = 1.5f;

    
    public float centerRadius = 19f;
    public float bridgeWidth = 5f;
    public float bridgeLength = 40f;

    
    public string serverIP = "127.0.0.1";
    public int tcpPort = 7777;
    public int udpPort = 7778;

    
    public int myPlayerId = -1;
    public GameObject[] playerPrefabs = new GameObject[4];
    public BumperCarController[] playerControllers = new BumperCarController[4];
    public Transform[] spawnPoints = new Transform[4];

    private UdpClient udpClient;
    private Thread receiveThread;
    private bool isRunning = false;

    // 가상 서버용 데이터
    private Vector3[] mockPositions = new Vector3[4];
    private Vector3[] mockVelocities = new Vector3[4];
    private float[] dashTimers = new float[4];
    private float[] dashCooldowns = new float[4];
    private Vector3[] dashDirections = new Vector3[4];

    private ConcurrentQueue<NetworkPackets.StatePacket> stateQueue = new ConcurrentQueue<NetworkPackets.StatePacket>();

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    private void Start()
    {
        InitializeMockData();

        if (useMockServer)
        {
            Debug.LogWarning("가상 서버 모드로 실행합니다.");
            if (myPlayerId == -1) myPlayerId = 0;
            SpawnPlayers();
            if (playerControllers[myPlayerId] != null) playerControllers[myPlayerId].gameObject.SetActive(true);
        }
        else
        {
            ConnectToServer();
        }
    }

    private void InitializeMockData()
    {
        for (int i = 0; i < 4; i++)
        {
            mockPositions[i] = GetInitialSpawnPosition(i);
            mockVelocities[i] = Vector3.zero;
            dashTimers[i] = 0f;
            dashCooldowns[i] = 0f;
            dashDirections[i] = Vector3.forward;
        }
    }

    private void ConnectToServer()
    {
        try
        {
            // TCP 매치메이킹 및 ID 할당
            TcpClient tcpClient = new TcpClient();
            tcpClient.Connect(serverIP, tcpPort);
            NetworkStream stream = tcpClient.GetStream();

            byte[] idBuffer = new byte[4];
            stream.Read(idBuffer, 0, idBuffer.Length);
            myPlayerId = BitConverter.ToInt32(idBuffer, 0);
            tcpClient.Close();

            Debug.Log($"[Network] 서버 접속 성공 할당된 ID: {myPlayerId}");

            if (myPlayerId == -1) return;

            SpawnPlayers();

            // UDP 통신 시작
            udpClient = new UdpClient();
            udpClient.Connect(serverIP, udpPort);
            isRunning = true;

            receiveThread = new Thread(ReceiveUdpData) { IsBackground = true };
            receiveThread.Start();

            // 서버 등록용 초기 패킷 전송
            NetworkPackets.InputPacket hello = new NetworkPackets.InputPacket
            {
                playerId = myPlayerId,
                playerName = "Player"
            };
            SendInputPacket(hello);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Network] 서버 접속 실패: {e.Message}");
        }
    }

    private Vector3 GetInitialSpawnPosition(int id)
    {
        if (spawnPoints != null && id < spawnPoints.Length && spawnPoints[id] != null) return spawnPoints[id].position;
        return new Vector3(id == 0 ? -10f : (id == 1 ? 10f : 0f), 5.2f, id == 2 ? -10f : (id == 3 ? 10f : 0f));
    }

    private Quaternion GetInitialSpawnRotation(int id)
    {
        if (spawnPoints != null && id < spawnPoints.Length && spawnPoints[id] != null) return spawnPoints[id].rotation;
        return Quaternion.identity;
    }

    private void SpawnPlayers()
    {
        for (int i = 0; i < 4; i++)
        {
            if (playerPrefabs[i] != null)
            {
                GameObject obj = Instantiate(playerPrefabs[i], GetInitialSpawnPosition(i), GetInitialSpawnRotation(i));
                playerControllers[i] = obj.GetComponent<BumperCarController>();
                playerControllers[i].playerId = i;
                playerControllers[i].isLocalPlayer = (i == myPlayerId);
                obj.SetActive(false);
            }
        }
    }

    private void Update()
    {
        // 가상 서버 타이머 업데이트
        if (useMockServer)
        {
            for (int i = 0; i < 4; i++)
            {
                if (dashCooldowns[i] > 0f) dashCooldowns[i] -= Time.deltaTime;
                if (dashTimers[i] > 0f) dashTimers[i] -= Time.deltaTime;
            }
        }

        // 수신된 패킷 처리 큐
        while (stateQueue.TryDequeue(out NetworkPackets.StatePacket packet))
        {
            if (packet.playerId < 0 || packet.playerId >= 4 || playerControllers[packet.playerId] == null) continue;

            if (!playerControllers[packet.playerId].gameObject.activeSelf)
            {
                playerControllers[packet.playerId].gameObject.SetActive(true);
            }

            playerControllers[packet.playerId].OnReceiveState(packet);
        }
    }

    public void SendInputPacket(NetworkPackets.InputPacket packet)
    {
        if (useMockServer)
        {
            SimulateServerLogic(packet);
            return;
        }

        if (!isRunning || udpClient == null) return;

        int size = Marshal.SizeOf(packet);
        byte[] buffer = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(packet, ptr, true);
            Marshal.Copy(ptr, buffer, 0, size);
            udpClient.Send(buffer, buffer.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void SimulateServerLogic(NetworkPackets.InputPacket input)
    {
        int id = input.playerId;
        if (id < 0 || id >= 4) return;

        float dt = Time.deltaTime;

        // 대시 처리
        if (input.isBoosting && dashCooldowns[id] <= 0f && dashTimers[id] <= 0f)
        {
            dashTimers[id] = dashDuration;
            dashCooldowns[id] = dashCooldown;

            Vector3 inputDir = new Vector3(input.inputX, 0f, input.inputY).normalized;
            dashDirections[id] = inputDir.magnitude < 0.1f ? (playerControllers[id] != null ? playerControllers[id].transform.forward : Vector3.forward) : inputDir;

            mockVelocities[id].x += dashDirections[id].x * dashImpulseForce;
            mockVelocities[id].z += dashDirections[id].z * dashImpulseForce;
        }

        // 이동 속도 적용
        if (dashTimers[id] > 0f)
        {
            mockVelocities[id].x += dashDirections[id].x * dashImpulseForce * 2f * dt;
            mockVelocities[id].z += dashDirections[id].z * dashImpulseForce * 2f * dt;
        }
        else if (input.isDriving)
        {
            mockVelocities[id].x += input.inputX * baseSpeed * dt;
            mockVelocities[id].z += input.inputY * baseSpeed * dt;
        }

        // 마찰력 및 위치 업데이트
        mockVelocities[id].x -= mockVelocities[id].x * drag * dt;
        mockVelocities[id].z -= mockVelocities[id].z * drag * dt;

        mockPositions[id].x += mockVelocities[id].x * dt;
        mockPositions[id].z += mockVelocities[id].z * dt;

        float rotY = 0f;
        if (Mathf.Abs(mockVelocities[id].x) > 0.1f || Mathf.Abs(mockVelocities[id].z) > 0.1f)
        {
            rotY = Mathf.Atan2(mockVelocities[id].x, mockVelocities[id].z) * Mathf.Rad2Deg;
        }

        // 안전 구역 판정
        float absX = Mathf.Abs(mockPositions[id].x);
        float absZ = Mathf.Abs(mockPositions[id].z);
        bool currentAlive = (new Vector2(absX, absZ).magnitude <= centerRadius) ||
                            (absZ <= bridgeWidth && absX <= bridgeLength) ||
                            (absX <= bridgeWidth && absZ <= bridgeLength);

        // 큐에 추가
        stateQueue.Enqueue(new NetworkPackets.StatePacket
        {
            playerId = id,
            playerName = input.playerName,
            posX = mockPositions[id].x,
            posY = mockPositions[id].y,
            posZ = mockPositions[id].z,
            rotY = rotY,
            velX = mockVelocities[id].x,
            velZ = mockVelocities[id].z,
            isBoosting = (dashTimers[id] > 0f),
            isAlive = currentAlive
        });
    }

    private void ReceiveUdpData()
    {
        IPEndPoint anyIP = new IPEndPoint(IPAddress.Any, 0);

        while (isRunning)
        {
            try
            {
                byte[] data = udpClient.Receive(ref anyIP);

                if (data.Length == Marshal.SizeOf(typeof(NetworkPackets.StatePacket)))
                {
                    IntPtr ptr = Marshal.AllocHGlobal(data.Length);
                    Marshal.Copy(data, 0, ptr, data.Length);
                    NetworkPackets.StatePacket packet = (NetworkPackets.StatePacket)Marshal.PtrToStructure(ptr, typeof(NetworkPackets.StatePacket));
                    Marshal.FreeHGlobal(ptr);

                    stateQueue.Enqueue(packet);
                }
            }
            catch (SocketException)
            {
                // 소켓이 정상적으로 닫힐 때 발생하는 예외 무시
                if (isRunning) Debug.LogError("[Network] UDP 수신 소켓 에러 발생");
            }
            catch (Exception e)
            {
                if (isRunning) Debug.LogError($"[Network] UDP 수신 스레드 에러: {e.Message}");
            }
        }
    }

    private void OnApplicationQuit()
    {
        isRunning = false;
        if (udpClient != null)
        {
            udpClient.Close();
        }
    }
}