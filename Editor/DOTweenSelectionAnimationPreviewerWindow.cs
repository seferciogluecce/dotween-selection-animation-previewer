using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public sealed class DOTweenSelectionAnimationPreviewerWindow : EditorWindow
{
    private const string WindowTitle = "DOTween Selection Animation Previewer";
    private const float MinimumWindowWidth = 300f;
    private const float MinimumWindowHeight = 220f;
    private const string SummaryText = "Preview DOTweenAnimation components under the Selection Root.";
    private const string MissingSelectionRootMessage = "Assign a Selection Root.";
    private const string MissingDotweenProMessage =
        "DOTween Pro is required to preview DOTweenAnimation components. Install and set up DOTween Pro, then reopen this window.";

    private readonly List<PreviewEntry> previews = new List<PreviewEntry>();

    private DotweenPreviewBridge bridge;
    private GameObject targetRoot;
    private GameObject previewRoot;
    private Vector2 scrollPosition;
    private bool includeInactive;
    private bool autoPlayOnly;
    private string statusMessage = MissingSelectionRootMessage;
    private MessageType statusType = MessageType.Info;
    private bool statusCanAutoUpdate = true;
    private GameObject lastIdleRoot;
    private bool lastIdleIncludeInactive;
    private bool lastIdleAutoPlayOnly;
    private bool lastIdlePlayModeUnavailable;
    private bool lastIdleRootValid;

    [MenuItem("Tools/DOTween/Selection Animation Previewer")]
    private static void Open()
    {
        GetWindow<DOTweenSelectionAnimationPreviewerWindow>(WindowTitle);
    }

    private void OnEnable()
    {
        minSize = new Vector2(MinimumWindowWidth, MinimumWindowHeight);
        bridge = DotweenPreviewBridge.Create();
        Selection.selectionChanged += OnSelectionChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.update += MonitorSelectionRoot;
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= OnSelectionChanged;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.update -= MonitorSelectionRoot;
        StopPreview();
    }

    private void OnGUI()
    {
        if (bridge == null)
        {
            bridge = DotweenPreviewBridge.Create();
        }

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        EditorGUILayout.LabelField("DOTween Selection Animation Previewer", EditorStyles.boldLabel);
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField(SummaryText, EditorStyles.wordWrappedMiniLabel);

        if (!bridge.IsAvailable)
        {
            DrawInlineMessage(MissingDotweenProMessage, MessageType.Warning);
            if (!string.IsNullOrEmpty(bridge.UnavailableReason))
            {
                DrawInlineMessage(bridge.UnavailableReason, MessageType.None);
            }

            EditorGUILayout.EndScrollView();
            return;
        }

        EditorGUI.BeginChangeCheck();
        GameObject newRoot = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Selection Root", "Scene or Prefab Mode root whose child DOTweenAnimation components will be previewed. Project prefab assets must be opened first."),
            targetRoot,
            typeof(GameObject),
            true);
        if (EditorGUI.EndChangeCheck())
        {
            SetTargetRoot(newRoot);
        }

        using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
        {
            if (GUILayout.Button("Use Selected", GUILayout.Height(24f)))
            {
                SetTargetRoot(Selection.activeGameObject);
            }
        }

        includeInactive = EditorGUILayout.ToggleLeft(
            new GUIContent("Include Inactive Objects", "Include DOTweenAnimation components on inactive child GameObjects."),
            includeInactive);
        autoPlayOnly = EditorGUILayout.ToggleLeft(
            new GUIContent("Only Include AutoPlay Animations", "Preview only DOTweenAnimation components with AutoPlay enabled."),
            autoPlayOnly);

        bool targetIsValid = IsValidSelectionRoot(targetRoot);
        int eligibleCount = CountEligibleAnimations(targetRoot);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Eligible Animations to Preview", EditorStyles.label, GUILayout.ExpandWidth(false));
            GUILayout.Label(eligibleCount.ToString(), EditorStyles.label, GUILayout.ExpandWidth(false));
        }
        EditorGUILayout.Space(6f);

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(
                       EditorApplication.isPlayingOrWillChangePlaymode || !targetIsValid || eligibleCount == 0))
            {
                if (GUILayout.Button("Play All Animations", GUILayout.Height(28f)))
                {
                    StartPreview(targetRoot);
                }
            }

            using (new EditorGUI.DisabledScope(previews.Count == 0))
            {
                if (GUILayout.Button("Stop and Restore Values", GUILayout.Height(28f)))
                {
                    StopPreview();
                }
            }
        }

        UpdateIdleStatus(targetRoot, targetIsValid, eligibleCount);

        EditorGUILayout.Space(6f);
        DrawStatus();

        EditorGUILayout.EndScrollView();
    }

    private void DrawStatus()
    {
        if (statusType == MessageType.Info && string.Equals(statusMessage, MissingSelectionRootMessage, StringComparison.Ordinal))
        {
            EditorGUILayout.LabelField(statusMessage, EditorStyles.wordWrappedMiniLabel);
            return;
        }

        DrawInlineMessage(statusMessage, statusType);
    }

    private static void DrawInlineMessage(string message, MessageType messageType)
    {
        if (string.IsNullOrEmpty(message))
            return;

        GUIStyle style = EditorStyles.wordWrappedMiniLabel;
        if (messageType == MessageType.Warning)
            style = EditorStyles.miniBoldLabel;
        else if (messageType == MessageType.Error)
            style = EditorStyles.boldLabel;

        EditorGUILayout.LabelField(message, style);
    }

    private void SetTargetRoot(GameObject root)
    {
        StopPreview();
        targetRoot = root;
        statusCanAutoUpdate = true;
        UpdateIdleStatus(targetRoot, IsValidSelectionRoot(targetRoot), CountEligibleAnimations(targetRoot));
        Repaint();
    }

    private int CountEligibleAnimations(GameObject root)
    {
        if (!IsValidSelectionRoot(root) || !bridge.IsAvailable)
        {
            return 0;
        }

        int count = 0;
        Component[] animations = bridge.GetAnimations(root, includeInactive);
        foreach (Component animation in animations)
        {
            if (IsEligible(animation))
            {
                count++;
            }
        }

        return count;
    }

    private bool IsEligible(Component animation)
    {
        return animation != null
               && bridge.GetIsActive(animation)
               && !bridge.IsAnimationTypeNone(animation)
               && (!autoPlayOnly || bridge.GetAutoPlay(animation));
    }

    private void StartPreview(GameObject root)
    {
        StopPreview();

        if (!bridge.IsAvailable)
        {
            StopPreview();
            statusMessage = MissingDotweenProMessage;
            statusType = MessageType.Warning;
            statusCanAutoUpdate = false;
            return;
        }

        if (!IsValidSelectionRoot(root))
        {
            statusMessage = GetInvalidRootMessage(root);
            statusType = root == null ? MessageType.Info : MessageType.Warning;
            statusCanAutoUpdate = false;
            return;
        }

        Component[] animations = bridge.GetAnimations(root, includeInactive);
        int failedCount = 0;

        try
        {
            bridge.StartPreview();
        }
        catch (Exception exception)
        {
            failedCount++;
            Debug.LogException(exception);
            statusMessage = "No animations could be previewed. Check the Console for 1 error(s).";
            statusType = MessageType.Error;
            statusCanAutoUpdate = false;
            return;
        }

        foreach (Component animation in animations)
        {
            if (!IsEligible(animation))
            {
                continue;
            }

            try
            {
                object tween = bridge.CreateEditorPreview(animation);
                if (tween == null)
                {
                    failedCount++;
                    continue;
                }

                previews.Add(new PreviewEntry(animation, tween, bridge.GetIsFrom(animation)));
                bridge.PrepareTweenForPreview(tween);
            }
            catch (Exception exception)
            {
                failedCount++;
                Debug.LogException(exception, animation);
            }
        }

        if (previews.Count == 0)
        {
            StopDotweenEditorPreview();
            statusMessage = failedCount > 0
                ? string.Format("No animations could be previewed. Check the Console for {0} error(s).", failedCount)
                : "No eligible DOTweenAnimation components were found under this Selection Root.";
            statusType = failedCount > 0 ? MessageType.Error : MessageType.Warning;
            statusCanAutoUpdate = false;
            return;
        }

        previewRoot = root;
        statusMessage = failedCount > 0
            ? string.Format("Previewing {0} animation(s); {1} could not be previewed. Check the Console for details.", previews.Count, failedCount)
            : string.Format("Previewing {0} animation(s).", previews.Count);
        statusType = failedCount > 0 ? MessageType.Warning : MessageType.Info;
        statusCanAutoUpdate = true;

        SceneView.RepaintAll();
        Repaint();
    }

    private void StopPreview()
    {
        for (int i = previews.Count - 1; i >= 0; i--)
        {
            PreviewEntry preview = previews[i];
            object tween = preview.Tween;

            if (tween == null || !bridge.IsTweenActive(tween))
            {
                continue;
            }

            try
            {
                if (preview.IsFrom)
                {
                    int loops = bridge.GetTweenLoops(tween);
                    if (loops < 0 || loops > 1)
                    {
                        bridge.TweenGoto(tween, bridge.GetTweenDuration(tween, false));
                    }
                    else
                    {
                        bridge.TweenComplete(tween);
                    }
                }
                else
                {
                    bridge.TweenRewind(tween);
                }

                bridge.TweenKill(tween);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, preview.Animation);
            }
        }

        bool hadPreviews = previews.Count > 0;
        previews.Clear();
        previewRoot = null;

        if (hadPreviews)
        {
            StopDotweenEditorPreview();
            statusMessage = "Preview stopped and animated values were restored.";
            statusType = MessageType.Info;
            statusCanAutoUpdate = false;
            SceneView.RepaintAll();
            Repaint();
        }
    }

    private void StopDotweenEditorPreview()
    {
        try
        {
            bridge.StopPreview();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
    }

    private void UpdateIdleStatus(GameObject root, bool rootIsValid, int eligibleCount)
    {
        bool playModeUnavailable = EditorApplication.isPlayingOrWillChangePlaymode;
        if (root != lastIdleRoot
            || rootIsValid != lastIdleRootValid
            || includeInactive != lastIdleIncludeInactive
            || autoPlayOnly != lastIdleAutoPlayOnly
            || playModeUnavailable != lastIdlePlayModeUnavailable)
        {
            statusCanAutoUpdate = true;
        }

        lastIdleRoot = root;
        lastIdleRootValid = rootIsValid;
        lastIdleIncludeInactive = includeInactive;
        lastIdleAutoPlayOnly = autoPlayOnly;
        lastIdlePlayModeUnavailable = playModeUnavailable;

        if (previews.Count > 0 || !statusCanAutoUpdate)
        {
            return;
        }

        if (playModeUnavailable)
        {
            statusMessage = "Preview is unavailable while entering or running Play Mode.";
            statusType = MessageType.Info;
            statusCanAutoUpdate = true;
        }
        else if (root == null)
        {
            statusMessage = MissingSelectionRootMessage;
            statusType = MessageType.Info;
            statusCanAutoUpdate = true;
        }
        else if (!rootIsValid)
        {
            statusMessage = GetInvalidRootMessage(root);
            statusType = MessageType.Warning;
            statusCanAutoUpdate = true;
        }
        else if (eligibleCount == 0)
        {
            statusMessage = "No eligible DOTweenAnimation components were found under this Selection Root.";
            statusType = MessageType.Warning;
            statusCanAutoUpdate = true;
        }
        else
        {
            statusMessage = string.Format("Ready to preview {0} eligible animation(s).", eligibleCount);
            statusType = MessageType.Info;
            statusCanAutoUpdate = true;
        }
    }

    private void MonitorSelectionRoot()
    {
        bool stoppedInvalidPreviewRoot = false;
        if (previews.Count > 0 && !IsValidSelectionRoot(previewRoot))
        {
            StopPreview();
            stoppedInvalidPreviewRoot = true;
        }

        if (targetRoot == null)
        {
            if (stoppedInvalidPreviewRoot)
            {
                statusMessage = "Selection Root is no longer available. Select or assign a Scene or Prefab Mode GameObject to begin.";
                statusType = MessageType.Info;
                statusCanAutoUpdate = false;
                Repaint();
            }

            return;
        }

        if (targetRoot != null && !EditorUtility.IsPersistent(targetRoot) && !targetRoot.scene.IsValid())
        {
            targetRoot = null;
            statusMessage = "Selection Root is no longer available. Select or assign a Scene or Prefab Mode GameObject to begin.";
            statusType = MessageType.Info;
            statusCanAutoUpdate = false;
            Repaint();
        }
    }

    private static bool IsValidSelectionRoot(GameObject root)
    {
        return root != null
               && !EditorUtility.IsPersistent(root)
               && root.scene.IsValid();
    }

    private static string GetInvalidRootMessage(GameObject root)
    {
        if (root == null)
        {
            return MissingSelectionRootMessage;
        }

        if (EditorUtility.IsPersistent(root))
        {
            return "Project-window prefab assets cannot be previewed directly. Open the prefab in Prefab Mode or select a Scene GameObject.";
        }

        return "Selection Root is no longer available. Select or assign a Scene or Prefab Mode GameObject to begin.";
    }

    private void OnSelectionChanged()
    {
        Repaint();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            StopPreview();
        }
    }

    private sealed class PreviewEntry
    {
        public readonly Component Animation;
        public readonly object Tween;
        public readonly bool IsFrom;

        public PreviewEntry(Component animation, object tween, bool isFrom)
        {
            Animation = animation;
            Tween = tween;
            IsFrom = isFrom;
        }
    }

    private sealed class DotweenPreviewBridge
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly Type animationType;
        private readonly MethodInfo getComponentsInChildrenMethod;
        private readonly FieldInfo isActiveField;
        private readonly FieldInfo animationTypeField;
        private readonly FieldInfo autoPlayField;
        private readonly FieldInfo isFromField;
        private readonly MethodInfo createEditorPreviewMethod;
        private readonly MethodInfo previewStartMethod;
        private readonly MethodInfo previewStopMethod;
        private readonly MethodInfo prepareTweenForPreviewMethod;
        private readonly PropertyInfo tweenActiveProperty;
        private readonly MethodInfo loopsMethod;
        private readonly MethodInfo durationMethod;
        private readonly MethodInfo gotoMethod;
        private readonly MethodInfo completeMethod;
        private readonly MethodInfo rewindMethod;
        private readonly MethodInfo killMethod;
        private readonly object[] oneBoolArgument = new object[1];
        private readonly object[] previewStartArguments = new object[1];
        private readonly object[] previewStopArguments = new object[2];
        private readonly object[] prepareTweenArguments = new object[4];
        private readonly object[] tweenOnlyArguments = new object[1];
        private readonly object[] tweenAndBoolArguments = new object[2];
        private readonly object[] tweenGotoArguments = new object[3];

        private DotweenPreviewBridge(
            Type animationType,
            MethodInfo getComponentsInChildrenMethod,
            FieldInfo isActiveField,
            FieldInfo animationTypeField,
            FieldInfo autoPlayField,
            FieldInfo isFromField,
            MethodInfo createEditorPreviewMethod,
            MethodInfo previewStartMethod,
            MethodInfo previewStopMethod,
            MethodInfo prepareTweenForPreviewMethod,
            PropertyInfo tweenActiveProperty,
            MethodInfo loopsMethod,
            MethodInfo durationMethod,
            MethodInfo gotoMethod,
            MethodInfo completeMethod,
            MethodInfo rewindMethod,
            MethodInfo killMethod)
        {
            this.animationType = animationType;
            this.getComponentsInChildrenMethod = getComponentsInChildrenMethod;
            this.isActiveField = isActiveField;
            this.animationTypeField = animationTypeField;
            this.autoPlayField = autoPlayField;
            this.isFromField = isFromField;
            this.createEditorPreviewMethod = createEditorPreviewMethod;
            this.previewStartMethod = previewStartMethod;
            this.previewStopMethod = previewStopMethod;
            this.prepareTweenForPreviewMethod = prepareTweenForPreviewMethod;
            this.tweenActiveProperty = tweenActiveProperty;
            this.loopsMethod = loopsMethod;
            this.durationMethod = durationMethod;
            this.gotoMethod = gotoMethod;
            this.completeMethod = completeMethod;
            this.rewindMethod = rewindMethod;
            this.killMethod = killMethod;
            IsAvailable = true;
        }

        private DotweenPreviewBridge(string unavailableReason)
        {
            UnavailableReason = unavailableReason;
        }

        public bool IsAvailable { get; }
        public string UnavailableReason { get; }

        public static DotweenPreviewBridge Create()
        {
            Type animationType = FindType("DG.Tweening.DOTweenAnimation");
            Type tweenType = FindType("DG.Tweening.Tween");
            Type tweenExtensionsType = FindType("DG.Tweening.TweenExtensions");
            Type editorPreviewType = FindType("DG.DOTweenEditor.DOTweenEditorPreview");

            if (animationType == null || tweenType == null || tweenExtensionsType == null || editorPreviewType == null)
            {
                return new DotweenPreviewBridge("Missing required DOTween Pro/editor preview types.");
            }

            MethodInfo getComponentsInChildrenMethod = FindGetComponentsInChildrenMethod();
            if (getComponentsInChildrenMethod == null)
            {
                return new DotweenPreviewBridge("Unity GetComponentsInChildren(bool) API was not found.");
            }

            getComponentsInChildrenMethod = getComponentsInChildrenMethod.MakeGenericMethod(animationType);

            FieldInfo isActiveField = animationType.GetField("isActive", InstanceFlags);
            FieldInfo animationTypeField = animationType.GetField("animationType", InstanceFlags);
            FieldInfo autoPlayField = animationType.GetField("autoPlay", InstanceFlags);
            FieldInfo isFromField = animationType.GetField("isFrom", InstanceFlags);
            MethodInfo createEditorPreviewMethod = animationType.GetMethod("CreateEditorPreview", InstanceFlags, null, Type.EmptyTypes, null);

            MethodInfo previewStartMethod = editorPreviewType.GetMethod("Start", StaticFlags, null, new[] { typeof(Action) }, null);
            MethodInfo previewStopMethod = editorPreviewType.GetMethod("Stop", StaticFlags, null, new[] { typeof(bool), typeof(bool) }, null);
            MethodInfo prepareTweenForPreviewMethod = editorPreviewType.GetMethod("PrepareTweenForPreview", StaticFlags, null, new[] { tweenType, typeof(bool), typeof(bool), typeof(bool) }, null);

            PropertyInfo tweenActiveProperty = tweenType.GetProperty("active", InstanceFlags);
            MethodInfo loopsMethod = tweenExtensionsType.GetMethod("Loops", StaticFlags, null, new[] { tweenType }, null);
            MethodInfo durationMethod = tweenExtensionsType.GetMethod("Duration", StaticFlags, null, new[] { tweenType, typeof(bool) }, null);
            MethodInfo gotoMethod = tweenExtensionsType.GetMethod("Goto", StaticFlags, null, new[] { tweenType, typeof(float), typeof(bool) }, null);
            MethodInfo completeMethod = tweenExtensionsType.GetMethod("Complete", StaticFlags, null, new[] { tweenType }, null);
            MethodInfo rewindMethod = tweenExtensionsType.GetMethod("Rewind", StaticFlags, null, new[] { tweenType, typeof(bool) }, null);
            MethodInfo killMethod = tweenExtensionsType.GetMethod("Kill", StaticFlags, null, new[] { tweenType, typeof(bool) }, null);

            if (isActiveField == null || animationTypeField == null || autoPlayField == null || isFromField == null
                || createEditorPreviewMethod == null || previewStartMethod == null || previewStopMethod == null
                || prepareTweenForPreviewMethod == null || tweenActiveProperty == null || loopsMethod == null
                || durationMethod == null || gotoMethod == null || completeMethod == null || rewindMethod == null
                || killMethod == null)
            {
                return new DotweenPreviewBridge("Installed DOTween Pro does not expose the editor preview API expected by this tool.");
            }

            return new DotweenPreviewBridge(
                animationType,
                getComponentsInChildrenMethod,
                isActiveField,
                animationTypeField,
                autoPlayField,
                isFromField,
                createEditorPreviewMethod,
                previewStartMethod,
                previewStopMethod,
                prepareTweenForPreviewMethod,
                tweenActiveProperty,
                loopsMethod,
                durationMethod,
                gotoMethod,
                completeMethod,
                rewindMethod,
                killMethod);
        }

        public Component[] GetAnimations(GameObject root, bool includeInactive)
        {
            if (root == null)
            {
                return Array.Empty<Component>();
            }

            oneBoolArgument[0] = includeInactive;
            Array animations = getComponentsInChildrenMethod.Invoke(root, oneBoolArgument) as Array;
            if (animations == null || animations.Length == 0)
            {
                return Array.Empty<Component>();
            }

            Component[] components = new Component[animations.Length];
            for (int i = 0; i < animations.Length; i++)
            {
                components[i] = animations.GetValue(i) as Component;
            }

            return components;
        }

        public bool GetIsActive(Component animation)
        {
            return GetBooleanField(isActiveField, animation);
        }

        public bool GetAutoPlay(Component animation)
        {
            return GetBooleanField(autoPlayField, animation);
        }

        public bool GetIsFrom(Component animation)
        {
            return GetBooleanField(isFromField, animation);
        }

        public bool IsAnimationTypeNone(Component animation)
        {
            object value = animationTypeField.GetValue(animation);
            return value == null || string.Equals(value.ToString(), "None", StringComparison.Ordinal);
        }

        public void StartPreview()
        {
            previewStartArguments[0] = null;
            previewStartMethod.Invoke(null, previewStartArguments);
        }

        public void StopPreview()
        {
            previewStopArguments[0] = false;
            previewStopArguments[1] = true;
            previewStopMethod.Invoke(null, previewStopArguments);
        }

        public object CreateEditorPreview(Component animation)
        {
            return createEditorPreviewMethod.Invoke(animation, null);
        }

        public void PrepareTweenForPreview(object tween)
        {
            prepareTweenArguments[0] = tween;
            prepareTweenArguments[1] = true;
            prepareTweenArguments[2] = true;
            prepareTweenArguments[3] = true;
            prepareTweenForPreviewMethod.Invoke(null, prepareTweenArguments);
        }

        public bool IsTweenActive(object tween)
        {
            if (tween == null)
            {
                return false;
            }

            object value = tweenActiveProperty.GetValue(tween, null);
            return value is bool active && active;
        }

        public int GetTweenLoops(object tween)
        {
            tweenOnlyArguments[0] = tween;
            object value = loopsMethod.Invoke(null, tweenOnlyArguments);
            return value is int loops ? loops : 0;
        }

        public float GetTweenDuration(object tween, bool includeLoops)
        {
            tweenAndBoolArguments[0] = tween;
            tweenAndBoolArguments[1] = includeLoops;
            object value = durationMethod.Invoke(null, tweenAndBoolArguments);
            return value is float duration ? duration : 0f;
        }

        public void TweenGoto(object tween, float position)
        {
            tweenGotoArguments[0] = tween;
            tweenGotoArguments[1] = position;
            tweenGotoArguments[2] = false;
            gotoMethod.Invoke(null, tweenGotoArguments);
        }

        public void TweenComplete(object tween)
        {
            tweenOnlyArguments[0] = tween;
            completeMethod.Invoke(null, tweenOnlyArguments);
        }

        public void TweenRewind(object tween)
        {
            tweenAndBoolArguments[0] = tween;
            tweenAndBoolArguments[1] = true;
            rewindMethod.Invoke(null, tweenAndBoolArguments);
        }

        public void TweenKill(object tween)
        {
            tweenAndBoolArguments[0] = tween;
            tweenAndBoolArguments[1] = false;
            killMethod.Invoke(null, tweenAndBoolArguments);
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static MethodInfo FindGetComponentsInChildrenMethod()
        {
            foreach (MethodInfo method in typeof(GameObject).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!method.IsGenericMethodDefinition || method.Name != "GetComponentsInChildren")
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
                {
                    return method;
                }
            }

            return null;
        }

        private static bool GetBooleanField(FieldInfo field, object target)
        {
            object value = field.GetValue(target);
            return value is bool boolValue && boolValue;
        }
    }
}
