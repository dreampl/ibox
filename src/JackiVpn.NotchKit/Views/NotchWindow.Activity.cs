using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JackiVpn.Domain.Activity;

namespace JackiVpn.Desktop.Views;

/// <summary>The activity circle at the island's right side (<see cref="ActivityCluster"/>).</summary>
public sealed partial class NotchWindow
{
    private readonly ActivityCluster _cluster = new();
    private readonly Dictionary<string, ActivityItem> _agentItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _agentIcons = new(StringComparer.Ordinal);
    private IReadOnlyList<ActivityItem> _activities = [];
    private DispatcherTimer? _spin;
    private DispatcherTimer? _closeStack;
    private bool _clusterHover;

    /// <summary>A circle behind the lead was clicked (open what it belongs to).</summary>
    public event EventHandler<ActivityItem>? ActivityClicked;

    /// <summary>The picture for a task (app icon, plugin icon); null shows its initial.</summary>
    public Func<ActivityItem, ImageSource?>? ActivityIcon { get; set; }

    /// <summary>Tasks shown now (tests).</summary>
    public int ActivityCount => _cluster.Count;

    public bool ActivityOpen => _cluster.IsOpen;

    private void InitActivity()
    {
        _body.Cluster = _cluster;
        _cluster.IconFor = item => _agentIcons.TryGetValue(item.Source, out var icon) && icon is not null ? icon : ActivityIcon?.Invoke(item);
        _cluster.EndRadius = CollapsedHeight / 2;

        // The drop snapping off tugs the edge a little; swallowed, it sends a small wave along it.
        _cluster.Detached += () =>
        {
            _body.Membrane.Impulse(0.95, -120, 0.07);
            Animate();
        };
        _cluster.Merged += () =>
        {
            _body.Membrane.Impulse(0.95, 240, 0.09);
            Animate();
        };
        _pill.MouseMove += (_, e) =>
        {
            if (_clusterHover && _cluster.HitTest(e.GetPosition(_body), _body.BodyWidth) is null)
            {
                _clusterHover = false;
                OnHover(true);
            }
        };
        _pill.MouseLeave += (_, _) =>
        {
            _clusterHover = false;
            if (_cluster.IsOpen)
            {
                _closeStack ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _closeStack.Tick -= CloseStack;
                _closeStack.Tick += CloseStack;
                _closeStack.Start();
            }
        };
        _pill.MouseEnter += (_, _) => _closeStack?.Stop();
    }

    private void CloseStack(object? sender, EventArgs e)
    {
        _closeStack?.Stop();
        _cluster.Close();
        AfterToggle();
    }

    /// <summary>The tasks from the activity bus (running first, newest first).</summary>
    public void SetActivities(IReadOnlyList<ActivityItem> items)
    {
        _activities = items;
        RefreshActivities();
    }

    /// <summary>
    /// What Claude ("claude") or ChatGPT / Codex ("chatgpt") is doing, for a notch without an activity bus (IBOX):
    /// working spins, waiting is amber, done and error stay a moment and leave.
    /// </summary>
    public void SetAgentActivity(string source, AgentActivity activity, ImageSource? icon = null)
    {
        var key = source == "claude" ? "claude" : "chatgpt";
        if (icon is not null)
        {
            _agentIcons[key] = icon;
        }

        if (activity == AgentActivity.None)
        {
            _agentItems.Remove(key);
        }
        else
        {
            _agentItems[key] = AgentItem(key, activity);
            if (activity is AgentActivity.Done or AgentActivity.Error)
            {
                var stamp = _agentItems[key];
                After(activity == AgentActivity.Done ? 6000 : 15000, () =>
                {
                    if (_agentItems.TryGetValue(key, out var now) && ReferenceEquals(now, stamp))
                    {
                        _agentItems.Remove(key);
                        RefreshActivities();
                    }
                });
            }
        }

        RefreshActivities();
    }

    /// <summary>An assistant's state as a task (never a number: how far an assistant is, nobody knows).</summary>
    public static ActivityItem AgentItem(string key, AgentActivity activity) => new()
    {
        Id = key,
        Source = key,
        Kind = "agent",
        Title = key == "claude" ? "Claude" : "ChatGPT",
        State = activity switch
        {
            AgentActivity.Waiting => ActivityState.Waiting,
            AgentActivity.Done => ActivityState.Done,
            AgentActivity.Error => ActivityState.Failed,
            _ => ActivityState.Running,
        },
        Detail = activity == AgentActivity.Working ? "working" : null,
        Started = DateTimeOffset.UtcNow,
        Updated = DateTimeOffset.UtcNow,
    };

    private void RefreshActivities()
    {
        var all = _activities.Concat(_agentItems.Values)
            .OrderBy(i => i.Finished)
            .ThenByDescending(i => i.State == ActivityState.Waiting)
            .ThenByDescending(i => i.Started)
            .ToList();
        _cluster.Set(all);
        Animate();
        SyncSpin();
    }

    private bool StepActivity(double dt) => _cluster.Step(dt);

    /// <summary>Test copies only: open or close the circles behind the lead without a pointer.</summary>
    internal void DemoToggleActivity()
    {
        _cluster.Toggle();
        AfterToggle();
    }

    /// <summary>A press on a circle: the lead opens or closes the others; one of them opens its app.</summary>
    private bool PressActivity(MouseButtonEventArgs e)
    {
        var hit = _cluster.HitTest(e.GetPosition(_body), _body.BodyWidth);
        if (hit is null)
        {
            return false;
        }

        e.Handled = true;
        if (hit == -1)
        {
            _cluster.Toggle();
            if (_cluster.IsOpen)
            {
                SetExpanded(false);
            }

            AfterToggle();
        }
        else if (_cluster.ItemAt(hit.Value) is { } item)
        {
            ActivityClicked?.Invoke(this, item);
        }

        return true;
    }

    /// <summary>The pointer came onto a circle, not the island: the island stays as it is.</summary>
    private bool HoverOnActivity()
    {
        if (_cluster.HitTest(Mouse.GetPosition(_body), _body.BodyWidth) is null)
        {
            return false;
        }

        _clusterHover = true;

        // The circles stay readable while the pointer is on them.
        _fadeTimer.Stop();
        if (_faded)
        {
            SetFaded(false);
        }

        return true;
    }

    /// <summary>Open circles keep the island awake; closed, it may fade again.</summary>
    private void AfterToggle()
    {
        if (_cluster.IsOpen)
        {
            _fadeTimer.Stop();
            if (_faded)
            {
                SetFaded(false);
            }
        }
        else
        {
            RestartFade();
        }

        Animate();
        SyncSpin();
    }

    /// <summary>
    /// An unknown amount turns its arc — only while such a circle is on screen, sixteen steps a second (a turning
    /// arc needs no more; a transparent window costs a copy per frame).
    /// </summary>
    private void SyncSpin()
    {
        var need = _cluster.NeedsSpin && IsVisible && !_hiddenForFullScreen && !Motion.Reduced;
        if (need && _spin is null)
        {
            _spin = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(62) };
            _spin.Tick += (_, _) =>
            {
                _cluster.Spin = (_cluster.Spin + 21) % 360;
                _body.Refresh();
                if (!_cluster.NeedsSpin || !IsVisible)
                {
                    SyncSpin();
                }
            };
            _spin.Start();
        }
        else if (!need && _spin is not null)
        {
            _spin.Stop();
            _spin = null;
        }
    }
}
