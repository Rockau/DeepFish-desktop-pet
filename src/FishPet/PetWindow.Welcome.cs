using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FishPet;

// 第一次真正出场时说欢迎（台词和规则见 Welcome.cs、welcome.txt）。
internal sealed partial class PetWindow
{
    private bool welcoming;

    // 她露面的时候调一下：还没欢迎过、不是自检用的看不见的桌宠、也不是收着待命，才会说。
    // Ready()（程序一开就出场）和 Wake()（本来收着、被叫出来）都会调到这里。
    private void WelcomeSoon()
    {
        if (settings.Welcomed || test || standby || welcoming) return;
        welcoming = true;
        _ = WelcomeAsync();
    }

    private async Task WelcomeAsync()
    {
        try
        {
            await Task.Delay(1800); // 让出场的问候动作先演完
            // 全屏的东西盖着、正在收起、还没画出来：等到她真的看得见（最多一分钟）。等不到就算了，下次出场再说，不记“说过了”。
            for (int wait = 0; wait < 120 && (standby || hiding || hiddenForFullscreen || !ready) && !exiting; wait++) await Task.Delay(500);
            if (exiting || standby || hiding || hiddenForFullscreen || !ready) return;
            settings.Welcomed = true;
            settings.Save();
            string[]? fileLines = null;
            try
            {
                var file = Path.Combine(Places.Root, "welcome.txt");
                if (File.Exists(file)) fileLines = File.ReadAllLines(file);
            }
            catch (Exception error) { Places.Log("welcome.txt 读不出来：" + error.Message); }
            Places.Log("第一次出场：说欢迎");
            foreach (var bubble in Welcome.Bubbles(Welcome.Text(fileLines)))
                _ = InTurn(() => { if (!exiting) SayLine(bubble); return Task.CompletedTask; }, CancellationToken.None);
        }
        finally { welcoming = false; }
    }
}
