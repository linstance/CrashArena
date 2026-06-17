using UnityEngine;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    private void Awake()
    {
        // 싱글톤 패턴 적용
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        // C++ 서버 틱(60Hz)과 클라이언트 화면 프레임을 60FPS로 완벽히 동기화
        QualitySettings.vSyncCount = 0; 
        Application.targetFrameRate = 60;
        
        Debug.Log("게임 환경 설정 완료: 60 FPS 고정");
    }
}