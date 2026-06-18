using UnityEngine;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    private void Awake()
    {
        
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

        
        QualitySettings.vSyncCount = 0; 
        Application.targetFrameRate = 60;
        
        Debug.Log("게임 환경 설정 완료: 60 FPS 고정");
    }
}