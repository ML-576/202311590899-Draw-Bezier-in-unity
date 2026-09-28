## 运行前准备（一次性）
由于工作目录里只有`Assets` ，需要在 Unity 里建一个工程把代码装进去：

1. 打开 Unity Hub ， New Project ，选 3D（Built-in Render Pipeline） 或 3D（URP） 都可，版本用你本机的 2022.3.62f3（其它 2020.3+ 也兼容）。
2. 建好工程后， 关闭 Unity ，打开新建项目所在工作目录，将下载的文件往下翻找到`Assets` 文件夹内的3个文件`Editor、Resources、Scripts`整体复制 到新项目根目录`Assets`下，覆盖同名文件夹。
3. 重新用 Unity 打开工程，等脚本编译完成（底部进度条消失）。
4. 顶部菜单会多出一项 「Bezier曲线」 ，点 「一键搭建运行场景」 。
5. 此时场景里会自动出现一个名为`CurveStudio` 的对象（挂着主控脚本）和`Main Camera` （已挂载自由相机组件）。
6. 按 Ctrl+S 保存场景，然后点顶部 Play 运行。 （如果不想用一键搭建，也可以手动：建一个空 GameObject 挂`CurveStudioManager` ，Main Camera 上不用手动加`FreeLookCamera` ，脚本会自动添加。）
