using System;
using System.Threading.Tasks;
using Game.Core;
using GameServer.Client;
using UnityEngine;

namespace Game.Meta
{
    /// <summary>What every page needs from the menu: services, toasts, navigation and a safe way to run server calls.</summary>
    public sealed class MenuContext
    {
        public MenuScreen Host;
        public GameSession Session;
        public ToastLayer Toasts;
        public RectTransform Overlay;
        public Action<int> Navigate;

        public void Toast(string text, Icon? icon = null) => Toasts.Show(text, Theme.Blue, icon);

        public void Error(Exception ex)
        {
            Debug.LogWarning("[Menu] " + ex);
            if (Host.HandleBlockingError(ex))
                return;

            Toasts.Show(Parts.ErrorText(ex), Theme.Red, null);
        }

        public void Reward(Bundle reward, string title = null)
        {
            if (reward == null)
                return;

            var head = Parts.Headline(reward);
            var text = (title != null ? title + "  " : "") + Parts.Describe(reward);
            Toasts.Show(text, Theme.Gold, head.Icon);
        }

        /// <summary>
        /// Runs a server call without blocking the screen: the button is locked while it runs, and a failure
        /// becomes a red toast instead of an exception.
        /// </summary>
        public async void Run(Func<Task> work, GButton button = null)
        {
            if (button != null)
                button.Interactable = false;

            try
            {
                await work();
            }
            catch (Exception ex)
            {
                Error(ex);
            }
            finally
            {
                if (button != null)
                    button.Interactable = true;
            }
        }
    }

    /// <summary>One screen of the menu (Shop, Battle...). Built once, refreshed whenever it is shown.</summary>
    public abstract class Page
    {
        protected MenuContext C { get; private set; }
        protected GameSession S => C.Session;

        public RectTransform Root { get; private set; }

        public void Init(MenuContext context, RectTransform root)
        {
            C = context;
            Root = root;
            Build();
        }

        protected abstract void Build();

        /// <summary>The page became visible: reload its data.</summary>
        public virtual void OnShow() { }

        public virtual void OnHide() { }

        public virtual void Dispose() { }
    }
}
