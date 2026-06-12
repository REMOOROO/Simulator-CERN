using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// Симуляция Большого адронного коллайдера — «пульт оператора».
///
/// Оператор выбирает сорта ионов для двух встречных пучков (16 реально
/// используемых в ускорителях ядер — от протона до урана), запускает цикл:
/// инжекция -> разгон -> стабильные пучки -> соударения -> сброс пучка.
///
/// Физика:
///  - энергия пучка на нуклон E = 6.8 * (Z/A) ТэВ (жёсткость магнитов LHC);
///  - энергия пары нуклонов sqrt(s_NN) = 2*sqrt(E1*E2) — для Pb-Pb получается
///    реальное значение 5.36 ТэВ;
///  - множественность частиц растёт с массой ядер; при столкновении тяжёлых
///    ионов образуется кварк-глюонная плазма (вспышка + сотни треков);
///  - треки заряженных частиц закручены полем детектора (r = p/qB),
///    нейтральные летят прямо; осколки-спектаторы уходят вдоль пучка.
///
/// Скрипт строит всю сцену сам: повесь на пустой GameObject и нажми Play.
/// Управление: МЫШЬ по кнопкам пульта (дублируется клавиатурой:
/// ПРОБЕЛ — пуск, X — сброс, B — поле, P — авто).
/// </summary>
public class ColliderSimulation : MonoBehaviour
{
    // ====================== Ионы (реально используемые) ======================

    private struct Ion
    {
        public string symbol, rus; public int Z, A;
        public Ion(string s, string r, int z, int a) { symbol = s; rus = r; Z = z; A = a; }
    }

    private readonly Ion[] ions = {
        new Ion("p",  "протон (водород)", 1, 1),     // LHC
        new Ion("d",  "дейтрон",          1, 2),     // RHIC
        new Ion("He", "гелий-4 (альфа)",  2, 4),     // SPS
        new Ion("C",  "углерод-12",       6, 12),    // NICA, медицина
        new Ion("O",  "кислород-16",      8, 16),    // LHC (сеанс 2025)
        new Ion("Ne", "неон-20",          10, 20),   // LHC (сеанс 2025)
        new Ion("Si", "кремний-28",       14, 28),   // AGS
        new Ion("Ar", "аргон-40",         18, 40),   // SPS
        new Ion("Ca", "кальций-40",       20, 40),   // FAIR
        new Ion("Cu", "медь-63",          29, 63),   // RHIC
        new Ion("Kr", "криптон-84",       36, 84),   // FAIR
        new Ion("Ru", "рутений-96",       44, 96),   // RHIC (изобарный сеанс)
        new Ion("Xe", "ксенон-129",       54, 129),  // LHC (сеанс 2017)
        new Ion("Au", "золото-197",       79, 197),  // RHIC
        new Ion("Pb", "свинец-208",       82, 208),  // LHC
        new Ion("U",  "уран-238",         92, 238),  // RHIC
    };

    private int ion1 = 14, ion2 = 14; // по умолчанию Pb-Pb

    // ====================== Параметры ======================

    public float ringRadius = 9f;
    public float detectorRadius = 5.5f;
    public float rampTime = 5f;
    public float maxAngularSpeed = 4.5f;
    public bool magneticFieldOn = true;
    public bool autoMode = true;

    private const float ProtonBeamTeV = 6.8f; // энергия протонного пучка LHC

    // ====================== Состояние ======================

    private enum State { Standby, Injection, Ramp, Stable, Event }
    private State state = State.Standby;

    private float beamAngle, speedFrac, phaseTimer;
    private int eventCount;
    private float eventTimer;
    private int rampMilestone;
    private bool qgpEvent, higgsEvent;
    private float lumi; // условная «набранная светимость»

    private Transform bunch1, bunch2;
    private Vector3 ip;
    private Light flash;
    private LineRenderer qgpRing;
    private float qgpT = 1e9f;

    private readonly List<TrackAnim> tracks = new List<TrackAnim>();
    private readonly List<string> log = new List<string>();
    private readonly List<string> particleSummary = new List<string>();

    private Text statusText, helpText, logText, eventText;
    private Text ion1Text, ion2Text, fieldBtnText, autoBtnText;

    private class TrackAnim
    {
        public LineRenderer lr; public Vector3[] pts; public float t, speed;
    }

    // ====================== Частицы-продукты ======================

    private struct Species
    {
        public string name; public int charge; public Color color; public float weight;
        public Species(string n, int q, Color c, float w) { name = n; charge = q; color = c; weight = w; }
    }

    private readonly Species[] species = {
        new Species("\u03c0\u207a", +1, new Color(1f, 0.85f, 0.2f), 22f),
        new Species("\u03c0\u207b", -1, new Color(1f, 0.65f, 0.1f), 22f),
        new Species("K\u207a",     +1, new Color(0.4f, 1f, 0.5f),   6f),
        new Species("K\u207b",     -1, new Color(0.2f, 0.85f, 0.4f),6f),
        new Species("p",           +1, new Color(1f, 0.35f, 0.3f),  5f),
        new Species("\u03bc\u207a",+1, new Color(0.5f, 0.7f, 1f),   2f),
        new Species("\u03bc\u207b",-1, new Color(0.35f, 0.55f, 1f), 2f),
        new Species("e\u207b",     -1, new Color(0.7f, 0.9f, 1f),   2f),
        new Species("\u03b3",       0, new Color(0.95f, 0.95f, 1f), 7f),
        new Species("\u039b\u2070", 0, new Color(0.6f, 1f, 0.9f),   2f),
        new Species("n",            0, new Color(0.6f, 0.6f, 0.65f),2f),
    };

    private Ion I1 => ions[ion1];
    private Ion I2 => ions[ion2];
    private float BeamE(Ion i) => ProtonBeamTeV * i.Z / i.A;       // ТэВ на нуклон
    private float SqrtSNN => 2f * Mathf.Sqrt(BeamE(I1) * BeamE(I2));

    // ==================================================================
    //  ПОСТРОЕНИЕ СЦЕНЫ
    // ==================================================================

    private void Start()
    {
        ip = new Vector3(ringRadius, 0.5f, 0f);
        BuildEnvironment();
        BuildRing();
        BuildDetector();
        BuildBunches();
        BuildCameraAndLight();
        BuildUI();
        AddLog("Система управления готова. Выберите ионы и нажмите ПУСК.");
        AddLog("Магниты охлаждены до 1.9 K. Вакуум в норме.");
    }

    private Material MakeMat(Color c)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("HDRP/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        return m;
    }

    private Material LineMat(Color c)
    {
        var sh = Shader.Find("Sprites/Default");
        var m = new Material(sh != null ? sh : Shader.Find("Unlit/Color"));
        m.color = c;
        return m;
    }

    private void BuildEnvironment()
    {
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(4.5f, 1f, 3.2f);
        ground.GetComponent<Renderer>().material = MakeMat(new Color(0.06f, 0.07f, 0.10f));
    }

    private LineRenderer MakeCircle(string name, Vector3 center, float radius,
                                    float width, Color c, int points = 128)
    {
        var go = new GameObject(name);
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = points;
        lr.loop = true;
        lr.useWorldSpace = true;
        lr.widthMultiplier = width;
        lr.material = LineMat(c);
        lr.startColor = c; lr.endColor = c;
        for (int i = 0; i < points; i++)
        {
            float a = i * Mathf.PI * 2f / points;
            lr.SetPosition(i, center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
        }
        return lr;
    }

    private void BuildRing()
    {
        MakeCircle("Ring", new Vector3(0, 0.5f, 0), ringRadius, 0.45f, new Color(0.25f, 0.3f, 0.4f));
        for (int i = 0; i < 28; i++)
        {
            float a = i * Mathf.PI * 2f / 28f;
            var p = new Vector3(Mathf.Cos(a) * ringRadius, 0.5f, Mathf.Sin(a) * ringRadius);
            if (Vector3.Distance(p, ip) < 2.2f) continue;
            var magnet = GameObject.CreatePrimitive(PrimitiveType.Cube);
            magnet.name = "Dipole_" + i;
            magnet.transform.position = p;
            magnet.transform.localScale = new Vector3(0.5f, 0.7f, 1.1f);
            magnet.transform.rotation = Quaternion.LookRotation(new Vector3(-Mathf.Sin(a), 0, Mathf.Cos(a)));
            magnet.GetComponent<Renderer>().material = MakeMat(new Color(0.15f, 0.35f, 0.8f));
            Destroy(magnet.GetComponent<Collider>());
        }
    }

    private void BuildDetector()
    {
        var c0 = new Vector3(ip.x, 0.06f, ip.z);
        MakeCircle("Tracker", c0, detectorRadius * 0.35f, 0.07f, new Color(0.5f, 0.55f, 0.65f));
        MakeCircle("ECal",    c0, detectorRadius * 0.62f, 0.10f, new Color(0.35f, 0.45f, 0.6f));
        MakeCircle("HCal",    c0, detectorRadius * 0.82f, 0.13f, new Color(0.3f, 0.38f, 0.52f));
        MakeCircle("MuonSys", c0, detectorRadius,         0.18f, new Color(0.45f, 0.35f, 0.6f));

        var pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pad.name = "DetectorPad";
        pad.transform.position = new Vector3(ip.x, 0.02f, ip.z);
        pad.transform.localScale = new Vector3(detectorRadius * 2.05f, 0.02f, detectorRadius * 2.05f);
        pad.GetComponent<Renderer>().material = MakeMat(new Color(0.10f, 0.11f, 0.15f));
        Destroy(pad.GetComponent<Collider>());

        // вспышка столкновения
        var fgo = new GameObject("Flash");
        fgo.transform.position = ip + Vector3.up * 1.5f;
        flash = fgo.AddComponent<Light>();
        flash.type = LightType.Point;
        flash.range = 14f;
        flash.color = new Color(1f, 0.8f, 0.5f);
        flash.intensity = 0f;

        // кольцо «файербола» кварк-глюонной плазмы
        qgpRing = MakeCircle("QGP", new Vector3(ip.x, 0.1f, ip.z), 0.1f, 0.35f,
                             new Color(1f, 0.55f, 0.15f, 0.9f), 96);
        qgpRing.enabled = false;
    }

    private Transform MakeBunch(string name, Color c)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.localScale = Vector3.one * 0.45f;
        go.GetComponent<Renderer>().material = MakeMat(c);
        Destroy(go.GetComponent<Collider>());
        var trail = go.AddComponent<TrailRenderer>();
        trail.time = 0.35f;
        trail.startWidth = 0.3f;
        trail.endWidth = 0.02f;
        trail.material = LineMat(c);
        trail.startColor = c;
        trail.endColor = new Color(c.r, c.g, c.b, 0f);
        go.SetActive(false);
        return go.transform;
    }

    private void BuildBunches()
    {
        bunch1 = MakeBunch("Beam1", new Color(1f, 0.3f, 0.25f));
        bunch2 = MakeBunch("Beam2", new Color(1f, 0.85f, 0.25f));
    }

    private void BuildCameraAndLight()
    {
        var cam = Camera.main;
        if (cam == null)
        {
            var cgo = new GameObject("Main Camera");
            cgo.tag = "MainCamera";
            cam = cgo.AddComponent<Camera>();
            cgo.AddComponent<AudioListener>();
        }
        cam.transform.position = new Vector3(3.5f, 21f, -14f);
        cam.transform.rotation = Quaternion.Euler(57f, 0f, 0f);
        cam.backgroundColor = new Color(0.02f, 0.03f, 0.05f);
        cam.clearFlags = CameraClearFlags.SolidColor;

        var lgo = new GameObject("Sun");
        var light = lgo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.25f;
        lgo.transform.rotation = Quaternion.Euler(60f, -30f, 0f);
    }

    // ====================== UI: ПУЛЬТ ОПЕРАТОРА ======================

    private Font UiFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

    private Text MakeText(Transform parent, Vector2 aMin, Vector2 aMax,
                          Vector2 oMin, Vector2 oMax, int size, TextAnchor align,
                          Color? color = null)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = UiFont;
        t.fontSize = size;
        t.color = color ?? Color.white;
        t.alignment = align;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        var rt = t.rectTransform;
        rt.anchorMin = aMin; rt.anchorMax = aMax;
        rt.offsetMin = oMin; rt.offsetMax = oMax;
        return t;
    }

    private Image MakePanel(Transform parent, Vector2 aMin, Vector2 aMax,
                            Vector2 oMin, Vector2 oMax, Color c)
    {
        var go = new GameObject("Panel");
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = c;
        var rt = img.rectTransform;
        rt.anchorMin = aMin; rt.anchorMax = aMax;
        rt.offsetMin = oMin; rt.offsetMax = oMax;
        return img;
    }

    private Text MakeButton(Transform parent, Vector2 pos, Vector2 size,
                            string label, Color bg, UnityEngine.Events.UnityAction onClick,
                            int fontSize = 24)
    {
        var go = new GameObject("Btn_" + label);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = bg;
        var rt = img.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0, 0);
        rt.pivot = new Vector2(0, 0);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.highlightedColor = new Color(bg.r * 1.35f, bg.g * 1.35f, bg.b * 1.35f, 1f);
        colors.pressedColor = new Color(bg.r * 0.6f, bg.g * 0.6f, bg.b * 0.6f, 1f);
        btn.colors = colors;
        btn.onClick.AddListener(onClick);

        var txt = MakeText(go.transform, Vector2.zero, Vector2.one,
                           Vector2.zero, Vector2.zero, fontSize, TextAnchor.MiddleCenter);
        txt.text = label;
        return txt;
    }

    private void BuildUI()
    {
        var cgo = new GameObject("Canvas");
        var canvas = cgo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = cgo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600, 900);
        cgo.AddComponent<GraphicRaycaster>();

        // EventSystem для кликов мышью (поддержка обеих систем ввода)
        var ego = new GameObject("EventSystem");
        ego.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        var module = ego.AddComponent<InputSystemUIInputModule>();
        module.AssignDefaultActions();
#else
        ego.AddComponent<StandaloneInputModule>();
#endif

        // ---------- верхние информационные панели ----------
        MakePanel(cgo.transform, new Vector2(0, 1), new Vector2(0, 1),
                  new Vector2(14, -250), new Vector2(640, -12), new Color(0.04f, 0.06f, 0.09f, 0.85f));
        statusText = MakeText(cgo.transform, new Vector2(0, 1), new Vector2(0, 1),
            new Vector2(26, -244), new Vector2(630, -18), 24, TextAnchor.UpperLeft);

        helpText = MakeText(cgo.transform, new Vector2(1, 1), new Vector2(1, 1),
            new Vector2(-520, -150), new Vector2(-16, -14), 18, TextAnchor.UpperRight,
            new Color(0.75f, 0.8f, 0.9f));
        helpText.text =
            "Пульт: мышь по кнопкам внизу\n" +
            "Дублирование: ПРОБЕЛ — пуск, X — сброс,\n" +
            "B — поле, P — авто";

        eventText = MakeText(cgo.transform, new Vector2(1, 0), new Vector2(1, 0),
            new Vector2(-560, 240), new Vector2(-20, 740), 20, TextAnchor.LowerRight,
            new Color(0.9f, 0.93f, 1f));

        // ---------- нижняя консоль («пульт») ----------
        var console = MakePanel(cgo.transform, new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(0, 0), new Vector2(0, 224), new Color(0.07f, 0.09f, 0.12f, 0.96f));
        MakePanel(console.transform, new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(0, -4), new Vector2(0, 0), new Color(0.2f, 0.55f, 0.9f, 0.9f)); // световая кромка

        var ct = console.transform;

        // --- выбор ионов ---
        MakeText(ct, Vector2.zero, Vector2.zero, new Vector2(24, 168), new Vector2(420, 204),
                 20, TextAnchor.MiddleLeft, new Color(0.7f, 0.78f, 0.9f)).text = "СОСТАВ ПУЧКОВ";

        MakeButton(ct, new Vector2(24, 110), new Vector2(46, 46), "<",
                   new Color(0.16f, 0.22f, 0.32f), () => CycleIon(ref ion1, -1));
        ion1Text = MakeText(ct, Vector2.zero, Vector2.zero,
                            new Vector2(76, 110), new Vector2(366, 156), 23, TextAnchor.MiddleCenter,
                            new Color(1f, 0.45f, 0.4f));
        MakeButton(ct, new Vector2(372, 110), new Vector2(46, 46), ">",
                   new Color(0.16f, 0.22f, 0.32f), () => CycleIon(ref ion1, +1));

        MakeButton(ct, new Vector2(24, 52), new Vector2(46, 46), "<",
                   new Color(0.16f, 0.22f, 0.32f), () => CycleIon(ref ion2, -1));
        ion2Text = MakeText(ct, Vector2.zero, Vector2.zero,
                            new Vector2(76, 52), new Vector2(366, 98), 23, TextAnchor.MiddleCenter,
                            new Color(1f, 0.85f, 0.35f));
        MakeButton(ct, new Vector2(372, 52), new Vector2(46, 46), ">",
                   new Color(0.16f, 0.22f, 0.32f), () => CycleIon(ref ion2, +1));

        // --- главные кнопки ---
        MakeButton(ct, new Vector2(450, 118), new Vector2(190, 72), "ПУСК",
                   new Color(0.1f, 0.45f, 0.18f), StartRun, 30);
        MakeButton(ct, new Vector2(450, 32), new Vector2(190, 72), "СБРОС ПУЧКА",
                   new Color(0.5f, 0.12f, 0.1f), DumpBeam, 22);

        fieldBtnText = MakeButton(ct, new Vector2(656, 118), new Vector2(190, 72), "",
                   new Color(0.15f, 0.2f, 0.38f), ToggleField, 20);
        autoBtnText = MakeButton(ct, new Vector2(656, 32), new Vector2(190, 72), "",
                   new Color(0.15f, 0.2f, 0.38f), ToggleAuto, 20);

        // --- журнал ---
        MakePanel(ct, new Vector2(0, 0), new Vector2(0, 0),
                  new Vector2(866, 14), new Vector2(1584, 210), new Color(0.02f, 0.05f, 0.03f, 0.95f));
        logText = MakeText(ct, Vector2.zero, Vector2.zero,
            new Vector2(880, 18), new Vector2(1578, 206), 18, TextAnchor.LowerLeft,
            new Color(0.45f, 1f, 0.55f));

        RefreshIonTexts();
        RefreshToggleTexts();
    }

    // ====================== ПУЛЬТ: обработчики ======================

    private string Sup(int n)
    {
        const string d = "\u2070\u00b9\u00b2\u00b3\u2074\u2075\u2076\u2077\u2078\u2079";
        string s = "", str = n.ToString();
        foreach (char c in str) s += d[c - '0'];
        return s;
    }

    private string IonLabel(Ion i) =>
        string.Format("{0}{1} — {2}  (Z={3})", Sup(i.A), i.symbol, i.rus, i.Z);

    private void CycleIon(ref int idx, int dir)
    {
        idx = (idx + dir + ions.Length) % ions.Length;
        RefreshIonTexts();
        AddLog(string.Format("Источник ионов перенастроен: пучки {0} + {1}.",
               Sup(I1.A) + I1.symbol, Sup(I2.A) + I2.symbol));
        if (state != State.Standby)
            AddLog("Изменения вступят в силу после сброса и нового пуска.");
    }

    private void RefreshIonTexts()
    {
        ion1Text.text = "Пучок 1:  " + IonLabel(I1);
        ion2Text.text = "Пучок 2:  " + IonLabel(I2);
    }

    private void RefreshToggleTexts()
    {
        fieldBtnText.text = magneticFieldOn ? "ПОЛЕ ДЕТЕКТОРА\nВКЛ (3.8 Тл)" : "ПОЛЕ ДЕТЕКТОРА\nВЫКЛ";
        autoBtnText.text = autoMode ? "РЕЖИМ\nАВТО" : "РЕЖИМ\nРУЧНОЙ";
    }

    private void ToggleField()
    {
        magneticFieldOn = !magneticFieldOn;
        RefreshToggleTexts();
        AddLog(magneticFieldOn ? "Соленоид детектора включён (3.8 Тл)."
                               : "Соленоид детектора выключен — треки будут прямыми.");
    }

    private void ToggleAuto()
    {
        autoMode = !autoMode;
        RefreshToggleTexts();
        AddLog(autoMode ? "Режим: автоматические циклы соударений."
                        : "Режим: ручной (каждый цикл — кнопкой ПУСК).");
    }

    private void StartRun()
    {
        if (state == State.Injection || state == State.Ramp) return;
        ClearTracks();
        qgpEvent = higgsEvent = false;
        beamAngle = Mathf.PI;
        speedFrac = 0.12f;
        phaseTimer = 1.6f;
        rampMilestone = 0;
        state = State.Injection;
        bunch1.gameObject.SetActive(true);
        bunch2.gameObject.SetActive(true);
        AddLog(string.Format("ИНЖЕКЦИЯ: пучки {0} и {1} введены в кольцо.",
               Sup(I1.A) + I1.symbol, Sup(I2.A) + I2.symbol));
    }

    private void DumpBeam()
    {
        if (state == State.Standby) return;
        state = State.Standby;
        bunch1.gameObject.SetActive(false);
        bunch2.gameObject.SetActive(false);
        ClearTracks();
        AddLog("СБРОС: пучок выведен в поглотитель. Кольцо свободно.");
    }

    private void AddLog(string msg)
    {
        log.Add(string.Format("[{0}] {1}", System.DateTime.Now.ToString("HH:mm:ss"), msg));
        while (log.Count > 9) log.RemoveAt(0);
        if (logText != null) logText.text = string.Join("\n", log);
    }

    // ==================================================================
    //  ЛОГИКА
    // ==================================================================

    private Vector3 OnRing(float a) =>
        new Vector3(Mathf.Cos(a) * ringRadius, 0.5f, Mathf.Sin(a) * ringRadius);

    private void PositionBunches()
    {
        bunch1.position = OnRing(beamAngle);
        bunch2.position = OnRing(-beamAngle);
    }

    private void Update()
    {
        HandleKeyboard();

        switch (state)
        {
            case State.Injection:
                beamAngle += maxAngularSpeed * speedFrac * Time.deltaTime;
                PositionBunches();
                phaseTimer -= Time.deltaTime;
                if (phaseTimer <= 0f)
                {
                    state = State.Ramp;
                    AddLog("РАЗГОН: подъём энергии магнитов начат…");
                }
                break;

            case State.Ramp:
                speedFrac = Mathf.Min(1f, speedFrac + Time.deltaTime / rampTime);
                beamAngle += maxAngularSpeed * speedFrac * Time.deltaTime;
                PositionBunches();
                int pct = (int)(speedFrac * 100f);
                if (pct >= 25 * (rampMilestone + 1) && rampMilestone < 3)
                {
                    rampMilestone++;
                    AddLog(string.Format("Разгон: {0}% — энергия пучка {1:F2} ТэВ/нуклон.",
                           25 * rampMilestone, BeamE(I1) * speedFrac * speedFrac));
                }
                if (speedFrac >= 1f)
                {
                    state = State.Stable;
                    AddLog(string.Format("СТАБИЛЬНЫЕ ПУЧКИ. \u221as(NN) = {0:F2} ТэВ. Детектор пишет данные.", SqrtSNN));
                }
                break;

            case State.Stable:
                beamAngle += maxAngularSpeed * Time.deltaTime;
                PositionBunches();
                if (Mathf.Repeat(beamAngle, Mathf.PI * 2f) < 0.06f)
                    DoCollision();
                break;

            case State.Event:
                foreach (var tr in tracks)
                {
                    tr.t = Mathf.Min(1f, tr.t + Time.deltaTime * tr.speed);
                    tr.lr.positionCount = Mathf.Max(2, (int)(tr.pts.Length * tr.t));
                    for (int i = 0; i < tr.lr.positionCount; i++)
                        tr.lr.SetPosition(i, tr.pts[i]);
                }
                eventTimer -= Time.deltaTime;
                if (eventTimer <= 0f)
                {
                    if (autoMode)
                    {
                        ClearTracks();
                        beamAngle = Mathf.PI;
                        bunch1.gameObject.SetActive(true);
                        bunch2.gameObject.SetActive(true);
                        state = State.Stable;
                    }
                    // в ручном режиме событие остаётся на экране до ПУСКа/СБРОСа
                }
                break;
        }

        // затухание вспышки и расширение «файербола» КГП
        if (flash.intensity > 0f)
            flash.intensity = Mathf.Max(0f, flash.intensity - Time.deltaTime * 9f);
        if (qgpRing.enabled)
        {
            qgpT += Time.deltaTime;
            float r = 0.2f + qgpT * 4.5f;
            var c = qgpRing.startColor;
            c.a = Mathf.Clamp01(1.1f - qgpT * 0.9f);
            qgpRing.startColor = c; qgpRing.endColor = c;
            var center = new Vector3(ip.x, 0.1f, ip.z);
            for (int i = 0; i < qgpRing.positionCount; i++)
            {
                float a = i * Mathf.PI * 2f / qgpRing.positionCount;
                qgpRing.SetPosition(i, center + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
            }
            if (c.a <= 0f) qgpRing.enabled = false;
        }

        UpdateStatus();
    }

    private void HandleKeyboard()
    {
        bool space = false, pKey = false, bKey = false, xKey = false;
#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null)
        {
            space = kb.spaceKey.wasPressedThisFrame;
            pKey = kb.pKey.wasPressedThisFrame;
            bKey = kb.bKey.wasPressedThisFrame;
            xKey = kb.xKey.wasPressedThisFrame;
        }
#else
        space = Input.GetKeyDown(KeyCode.Space);
        pKey = Input.GetKeyDown(KeyCode.P);
        bKey = Input.GetKeyDown(KeyCode.B);
        xKey = Input.GetKeyDown(KeyCode.X);
#endif
        if (space) StartRun();
        if (xKey) DumpBeam();
        if (bKey) ToggleField();
        if (pKey) ToggleAuto();
    }

    // ====================== СОУДАРЕНИЕ ======================

    private void DoCollision()
    {
        state = State.Event;
        eventCount++;
        eventTimer = 4.5f;
        bunch1.gameObject.SetActive(false);
        bunch2.gameObject.SetActive(false);
        particleSummary.Clear();

        flash.intensity = 6f;

        int massSum = I1.A + I2.A;
        qgpEvent = massSum >= 32; // тяжёлые ионы -> кварк-глюонная плазма
        if (qgpEvent)
        {
            qgpRing.enabled = true;
            qgpT = 0f;
            AddLog(string.Format("Событие #{0}: файербол КГП, T ~ 5\u00b710\u00b9\u00b2 K!", eventCount));
        }
        else
        {
            AddLog(string.Format("Событие #{0} зарегистрировано.", eventCount));
        }

        // множественность ~ числу участвующих нуклонов
        int n = Mathf.Min(150, 7 + (int)(Mathf.Sqrt(I1.A * (float)I2.A) * 0.9f) + (int)(SqrtSNN * 0.6f));

        // редкое событие H -> γγ только в p-p на полной энергии
        higgsEvent = I1.A == 1 && I2.A == 1 && Random.value < 0.15f;
        if (higgsEvent)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            var photon = new Species("\u03b3 (E!)", 0, new Color(1f, 1f, 0.6f), 0);
            SpawnTrack(photon, a, 3.2f, 0.16f);
            SpawnTrack(photon, a + Mathf.PI + Random.Range(-0.3f, 0.3f), 3.2f, 0.16f);
            AddLog("ТРИГГЕР: пара фотонов высокой энергии — кандидат H \u2192 \u03b3\u03b3!");
        }

        var counts = new Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            var sp = PickSpecies();
            SpawnTrack(sp, Random.Range(0f, Mathf.PI * 2f), Random.Range(0.4f, 3.0f), 0.06f);
            counts[sp.name] = counts.TryGetValue(sp.name, out int c) ? c + 1 : 1;
        }

        // осколки-спектаторы вдоль направления пучков (для ядер)
        if (I1.A > 1) SpawnSpectator(+1);
        if (I2.A > 1) SpawnSpectator(-1);

        particleSummary.Add(string.Format("Событие #{0}:  {1} + {2},  \u221as(NN) = {3:F2} ТэВ",
            eventCount, Sup(I1.A) + I1.symbol, Sup(I2.A) + I2.symbol, SqrtSNN));
        particleSummary.Add(string.Format("Рождено частиц: {0}{1}", n,
            qgpEvent ? "  (кварк-глюонная плазма)" : ""));
        if (higgsEvent) particleSummary.Add("<b>Кандидат в бозон Хиггса: H \u2192 \u03b3\u03b3</b>");
        foreach (var kv in counts)
            particleSummary.Add(string.Format("{0} \u00d7 {1}", kv.Key, kv.Value));

        lumi += 0.37f;
    }

    private Species PickSpecies()
    {
        float total = 0f;
        foreach (var s in species) total += s.weight;
        float r = Random.value * total;
        foreach (var s in species)
        {
            r -= s.weight;
            if (r <= 0f) return s;
        }
        return species[0];
    }

    private void SpawnTrack(Species sp, float dirAngle, float p, float width)
    {
        var go = new GameObject("Track");
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.widthMultiplier = width;
        lr.material = LineMat(sp.color);
        lr.startColor = sp.color;
        var faded = sp.color; faded.a = 0.25f;
        lr.endColor = faded;

        var origin = new Vector3(ip.x, 0.12f, ip.z);
        var dir = new Vector3(Mathf.Cos(dirAngle), 0f, Mathf.Sin(dirAngle));
        var pts = new List<Vector3>();

        if (sp.charge == 0 || !magneticFieldOn)
        {
            for (int i = 0; i <= 40; i++)
                pts.Add(origin + dir * (detectorRadius * i / 40f));
        }
        else
        {
            float r = Mathf.Clamp(p * 2.0f, 0.9f, 14f);
            var perp = new Vector3(-dir.z, 0f, dir.x) * sp.charge;
            var center = origin + perp * r;
            float a0 = Mathf.Atan2(origin.z - center.z, origin.x - center.x);
            for (int i = 0; i <= 70; i++)
            {
                float a = a0 - sp.charge * Mathf.PI * 1.6f * i / 70f;
                var pt = center + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                pt.y = 0.12f;
                pts.Add(pt);
                if (Vector3.Distance(pt, origin) > detectorRadius) break;
            }
        }

        lr.positionCount = 2;
        lr.SetPosition(0, origin);
        lr.SetPosition(1, origin);
        tracks.Add(new TrackAnim { lr = lr, pts = pts.ToArray(), t = 0f, speed = Random.Range(1.7f, 2.8f) });
    }

    private void SpawnSpectator(int side)
    {
        // осколки ядра продолжают лететь по касательной к кольцу
        var sp = new Species("осколок", 0, new Color(0.55f, 0.55f, 0.6f), 0);
        var go = new GameObject("Spectator");
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.widthMultiplier = 0.14f;
        lr.material = LineMat(sp.color);
        lr.startColor = sp.color;
        lr.endColor = new Color(sp.color.r, sp.color.g, sp.color.b, 0f);

        var origin = new Vector3(ip.x, 0.12f, ip.z);
        var dir = new Vector3(0f, 0f, side); // касательная к кольцу в точке IP
        var pts = new List<Vector3>();
        for (int i = 0; i <= 30; i++)
            pts.Add(origin + dir * (detectorRadius * 1.15f * i / 30f));

        lr.positionCount = 2;
        lr.SetPosition(0, origin);
        lr.SetPosition(1, origin);
        tracks.Add(new TrackAnim { lr = lr, pts = pts.ToArray(), t = 0f, speed = 3.5f });
    }

    private void ClearTracks()
    {
        foreach (var tr in tracks) Destroy(tr.lr.gameObject);
        tracks.Clear();
        eventText.text = "";
    }

    // ====================== СТАТУС ======================

    private void UpdateStatus()
    {
        string st = state switch
        {
            State.Standby => "ожидание команд оператора",
            State.Injection => "ИНЖЕКЦИЯ пучков",
            State.Ramp => string.Format("РАЗГОН… {0:F0}%", speedFrac * 100f),
            State.Stable => "СТАБИЛЬНЫЕ ПУЧКИ — набор данных",
            State.Event => "СОУДАРЕНИЕ — реконструкция события",
            _ => ""
        };

        statusText.text =
            "<b>LHC — ПУЛЬТ ОПЕРАТОРА</b>   смена: " + System.DateTime.Now.ToString("HH:mm") + "\n" +
            string.Format("Сеанс: {0} + {1}\n", Sup(I1.A) + I1.symbol, Sup(I2.A) + I2.symbol) +
            string.Format("Состояние: {0}\n", st) +
            string.Format("Энергия: {0:F2} / {1:F2} ТэВ на нуклон   \u221as(NN) = {2:F2} ТэВ\n",
                BeamE(I1) * speedFrac * speedFrac, BeamE(I2) * speedFrac * speedFrac, SqrtSNN) +
            string.Format("События: {0}    Светимость: {1:F1} нб\u207b\u00b9\n", eventCount, lumi) +
            "Магниты: 1.9 K  \u2713    Вакуум: 10\u207b\u00b9\u2070 мбар  \u2713";

        if (state == State.Event && particleSummary.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            foreach (var line in particleSummary)
            {
                sb.AppendLine(line);
                if (++shown >= 15) { sb.AppendLine("…"); break; }
            }
            eventText.text = sb.ToString();
        }
    }
}
