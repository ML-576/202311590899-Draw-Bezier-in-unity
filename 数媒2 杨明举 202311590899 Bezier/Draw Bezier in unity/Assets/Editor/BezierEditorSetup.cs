#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using BezierStudio;

/// <summary>
/// 1) 一键搭建运行场景（相机 + 总控对象）。
/// 2) Esc 退出播放模式保险：未在程序内确认时，停止播放会弹出
///    不可被 Esc 跳过的确认框，需手动确认才真正退出。
/// </summary>
[InitializeOnLoad]
public static class BezierEditorSetup
{
    private const string QuitPrefKey = "BezierStudio_ConfirmedQuit";

    static BezierEditorSetup()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            EditorPrefs.DeleteKey(QuitPrefKey);
            return;
        }

        if (state == PlayModeStateChange.ExitingPlayMode)
        {
            // 程序内“确认退出”已置位，直接放行
            if (EditorPrefs.GetInt(QuitPrefKey, 0) == 1)
            {
                EditorPrefs.DeleteKey(QuitPrefKey);
                return;
            }

            bool confirmed = false;
            try
            {
                confirmed = EditorUtility.DisplayDialog(
                    "退出演示确认",
                    "检测到正在退出演示 / 游玩模式。\n\n确定要退出吗？\n（该确认框无法用 Esc 跳过，需手动点击）",
                    "确认退出",
                    "继续演示");
            }
            catch
            {
                confirmed = true; // 极少数状态下无法弹窗时放行，避免死锁
            }

            if (!confirmed)
            {
                // 中止退出，继续播放（delayCall 避免在状态回调里重入）
                EditorApplication.delayCall += () =>
                {
                    if (!EditorApplication.isPlaying)
                        EditorApplication.isPlaying = true;
                };
            }
        }
    }

    [MenuItem("Bezier曲线/一键搭建运行场景")]
    public static void BuildScene()
    {
        // 相机
        Camera cam = Camera.main;
        if (cam == null)
        {
            var existing = Object.FindObjectOfType<Camera>();
            if (existing != null) cam = existing;
        }
        if (cam == null)
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.13f, 0.13f, 0.15f);
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 2000f;
        }

        // 总控
        var manager = Object.FindObjectOfType<CurveStudioManager>();
        GameObject managerGo;
        if (manager == null)
        {
            managerGo = new GameObject("CurveStudio");
            managerGo.AddComponent<CurveStudioManager>();
        }
        else
        {
            managerGo = manager.gameObject;
        }

        Selection.activeGameObject = managerGo;
        EditorSceneManager.MarkSceneDirty(managerGo.scene);
        Debug.Log("[BezierStudio] 场景搭建完成：点击 Play 即可运行。F1/F2/F3 切换三个界面。");
    }
}
#endif
