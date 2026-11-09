cmake_minimum_required(VERSION 3.20)
project(podsvetka-click VERSION 1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)

find_package(ECM REQUIRED NO_MODULE)
set(CMAKE_MODULE_PATH \${ECM_MODULE_PATH})
include(KDEInstallDirs)
include(KDECMakeSettings)
include(KDECompilerSettings NO_POLICY_SCOPE)

find_package(Qt6 REQUIRED COMPONENTS Core Gui)
find_package(KF6CoreAddons REQUIRED)
find_package(KWin REQUIRED)

ecm_setup_qtplugin_macro_names(
    JSON_ARG2 "KWIN_EFFECT_FACTORY"
    CONFIG_CODE_VARIABLE PACKAGE_SETUP_KWINEFFECTS_AUTOMOC_VARIABLES
)

kcoreaddons_add_plugin(podsvetka-click
    SOURCES podsvetka_click.cpp
    INSTALL_NAMESPACE "kwin/effects/plugins"
)

target_link_libraries(podsvetka-click PRIVATE
    KWin::kwin
    Qt6::Core
    Qt6::Gui
)
