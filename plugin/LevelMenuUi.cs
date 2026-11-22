using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AyceOc2Bridge;

public sealed partial class Plugin
{
    private sealed class LevelMenuUi
    {
        private readonly ConstructorInfo newUiObject;
        private readonly Array rectComponents;
        private readonly Type vector2;
        private readonly Type color;
        private readonly MethodInfo findCanvas;
        private readonly MethodInfo findRect;
        private readonly MethodInfo addText;
        private readonly MethodInfo addImage;
        private readonly MethodInfo addButton;
        private readonly MethodInfo setParent;
        private readonly MethodInfo setActive;
        private readonly MethodInfo destroy;
        private readonly MethodInfo createFont;
        private readonly MethodInfo openCampaign;
        private readonly MethodInfo castExtras;
        private readonly MethodInfo findFrontends;
        private readonly Type frontendMenuType;
        private readonly object extraTrimmings;
        private readonly object keepFont;
        private readonly object middleLeft;
        private readonly object middleCenter;

        internal Type GameObject { get; }
        internal Type Button { get; }
        internal Type Selectable { get; }
        internal object ExplicitNavigation { get; }
        internal MethodInfo AddSelectable { get; }
        internal MethodInfo Focus { get; }

        internal LevelMenuUi(Assembly game, Assembly unity)
        {
            var ui = Assembly.Load("UnityEngine.UI");
            var canvasModule = Assembly.Load("UnityEngine.UIModule");
            var textModule = Assembly.Load("UnityEngine.TextRenderingModule");
            var interop = Assembly.Load("Il2CppInterop.Runtime");
            var nativeType = Assembly.Load("Il2Cppmscorlib").GetType("Il2CppSystem.Type", throwOnError: true);
            var il2CppType = interop.GetType("Il2CppInterop.Runtime.Il2CppType", throwOnError: true);
            var rect = unity.GetType("UnityEngine.RectTransform", throwOnError: true);
            var transform = unity.GetType("UnityEngine.Transform", throwOnError: true);
            var unityObject = unity.GetType("UnityEngine.Object", throwOnError: true);
            keepFont = Enum.Parse(unity.GetType("UnityEngine.HideFlags", throwOnError: true), "DontUnloadUnusedAsset");
            vector2 = unity.GetType("UnityEngine.Vector2", throwOnError: true);
            color = unity.GetType("UnityEngine.Color", throwOnError: true);
            GameObject = unity.GetType("UnityEngine.GameObject", throwOnError: true);
            Button = ui.GetType("UnityEngine.UI.Button", throwOnError: true);
            Selectable = ui.GetType("UnityEngine.UI.Selectable", throwOnError: true);
            newUiObject = GameObject.GetConstructor(new[] { typeof(string), nativeType.MakeArrayType() })
                ?? throw new MissingMethodException(GameObject.FullName, ".ctor(string, Il2CppSystem.Type[])");
            rectComponents = Array.CreateInstance(nativeType, 1);
            rectComponents.SetValue(Find(il2CppType, "From", new[] { typeof(Type) }).Invoke(null, new object[] { rect }), 0);
            findRect = GenericComponent("GetComponent", rect);
            findCanvas = GenericComponent("GetComponent", canvasModule.GetType("UnityEngine.Canvas", throwOnError: true));
            addText = GenericComponent("AddComponent", ui.GetType("UnityEngine.UI.Text", throwOnError: true));
            addImage = GenericComponent("AddComponent", ui.GetType("UnityEngine.UI.Image", throwOnError: true));
            addButton = GenericComponent("AddComponent", Button);
            setParent = Find(transform, "SetParent", new[] { transform, typeof(bool) });
            setActive = Find(GameObject, "SetActive", new[] { typeof(bool) });
            destroy = Find(unityObject, "Destroy", new[] { unityObject });
            createFont = Find(textModule.GetType("UnityEngine.Font", throwOnError: true), "CreateDynamicFontFromOSFont", new[] { typeof(string), typeof(int) });
            var anchor = textModule.GetType("UnityEngine.TextAnchor", throwOnError: true);
            middleCenter = Enum.Parse(anchor, "MiddleCenter");
            middleLeft = Enum.Parse(anchor, "MiddleLeft");
            ExplicitNavigation = Enum.Parse(ui.GetType("UnityEngine.UI.Navigation+Mode", throwOnError: true), "Explicit");
            var behaviour = game.GetType("BaseMenuBehaviour", throwOnError: true);
            AddSelectable = Find(behaviour, "AddAllowedSelectables", new[] { Selectable });
            Focus = Find(game.GetType("T17EventSystem", throwOnError: true), "SetSelectedGameObject", new[] { GameObject });
            var root = game.GetType("FrontendRootMenu", throwOnError: true);
            frontendMenuType = game.GetType("FrontendRootMenu+FrontendMenuTypeToOpen", throwOnError: true);
            extraTrimmings = Enum.Parse(frontendMenuType, "ExtraTrimmings");
            openCampaign = Find(root, "OpenFrontendMenu", new[] { frontendMenuType, typeof(int), typeof(bool) });
            var nativeObject = interop.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", throwOnError: true);
            var tryCast = nativeObject.GetMethods().Single(method => method.Name == "TryCast" && method.IsGenericMethodDefinition);
            castExtras = tryCast.MakeGenericMethod(game.GetType("ExtraTrimmingsMenu", throwOnError: true));
            findFrontends = unityObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == "FindObjectsOfType" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
                .MakeGenericMethod(root);
        }

        private MethodInfo GenericComponent(string method, Type component) => GameObject.GetMethods()
            .Single(candidate => candidate.Name == method && candidate.IsGenericMethodDefinition && candidate.GetParameters().Length == 0)
            .MakeGenericMethod(component);

        internal object FindCanvas(object transform)
        {
            for (var current = transform; current != null; current = Get(current, "parent"))
                if (findCanvas.Invoke(Get(current, "gameObject"), null) != null)
                    return current;
            throw new InvalidDataException("Could not find the extra-content canvas.");
        }

        internal object Node(object parent, string name, float left, float bottom, float right, float top)
        {
            var item = newUiObject.Invoke(new object[] { name, rectComponents });
            try
            {
                var rectangle = findRect.Invoke(item, null);
                setParent.Invoke(rectangle, new[] { parent, false });
                Set(item, "layer", Get(Get(parent, "gameObject"), "layer"));
                Set(rectangle, "anchorMin", Vec2(left, bottom));
                Set(rectangle, "anchorMax", Vec2(right, top));
                Set(rectangle, "offsetMin", Vec2(0f, 0f));
                Set(rectangle, "offsetMax", Vec2(0f, 0f));
                return item;
            }
            catch
            {
                Delete(item);
                throw;
            }
        }

        internal object Paint(object item, float r, float g, float b, float a)
        {
            var image = addImage.Invoke(item, null);
            Set(image, "color", Activator.CreateInstance(color, r, g, b, a));
            return image;
        }

        internal object CreateFont()
        {
            var font = createFont.Invoke(null, new object[] { "Microsoft YaHei UI", 28 })
                ?? throw new InvalidDataException("Could not load the Windows UI font.");
            Set(font, "hideFlags", keepFont);
            return font;
        }

        internal void ReleaseFont(object font) => destroy.Invoke(null, new[] { font });

        internal void OpenExtraContent(object main)
        {
            var handle = Handle(SceneOf(main));
            var root = Enumerate(findFrontends.Invoke(null, null)).Single(menu => Handle(SceneOf(menu)) == handle);
            var (_, index) = FindMenu(root, extraTrimmings, castExtras);
            openCampaign.Invoke(root, new[] { extraTrimmings, index, false });
        }

        private (object Menu, int Index) FindMenu(object root, object tab, MethodInfo cast)
        {
            var tabs = Get(root, "m_FrontEndTabableMenuTypes");
            var container = Find(tabs.GetType(), "get_Item", new[] { frontendMenuType }).Invoke(tabs, new[] { tab });
            var menus = SnapshotNativeList(Get(container, "m_Menus"));
            return menus.Select((menu, index) => (Menu: menu == null ? null : cast.Invoke(menu, null), Index: index))
                .Single(item => item.Menu != null);
        }

        internal object Label(object parent, object font, string text, float left, float bottom, float right, float top,
            int size, bool centered)
        {
            var label = addText.Invoke(Node(parent, "Label", left, bottom, right, top), null);
            Set(label, "font", font);
            Set(label, "fontSize", size);
            Set(label, "resizeTextForBestFit", true);
            Set(label, "resizeTextMinSize", 14);
            Set(label, "resizeTextMaxSize", size);
            Set(label, "alignment", centered ? middleCenter : middleLeft);
            Set(label, "raycastTarget", false);
            Set(label, "supportRichText", false);
            Set(label, "text", text);
            Set(label, "color", Activator.CreateInstance(color, .95f, .97f, 1f, 1f));
            return label;
        }

        internal object CreateButton(object parent, object font, string text, float left, float bottom, float right, float top)
        {
            var item = Node(parent, "Bridge button", left, bottom, right, top);
            try
            {
                var image = Paint(item, .20f, .38f, .48f, 1f);
                var button = addButton.Invoke(item, null);
                Set(button, "targetGraphic", image);
                Label(Get(item, "transform"), font, text, .06f, .04f, .94f, .96f, 26, centered: true);
                return button;
            }
            catch
            {
                Delete(item);
                throw;
            }
        }

        internal void Connect(object button, object up, object down)
        {
            var navigation = Get(button, "navigation");
            Set(navigation, "mode", ExplicitNavigation);
            Set(navigation, "selectOnUp", up);
            Set(navigation, "selectOnDown", down);
            Set(navigation, "selectOnLeft", up);
            Set(navigation, "selectOnRight", down);
            Set(button, "navigation", navigation);
        }

        internal void Activate(object item, bool active) => setActive.Invoke(item, new object[] { active });

        internal void Delete(object item)
        {
            Activate(item, false);
            destroy.Invoke(null, new[] { item });
        }

        private object Vec2(float x, float y) => Activator.CreateInstance(vector2, x, y);
    }
}
