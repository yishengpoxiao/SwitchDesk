#pragma once
namespace sphaira::ui { struct ProgressBox; }
namespace switchdesk {
void Launch();
void LaunchDbiFromConsole(sphaira::ui::ProgressBox* box);
void SetProgress(long long completed, long long total);
}
