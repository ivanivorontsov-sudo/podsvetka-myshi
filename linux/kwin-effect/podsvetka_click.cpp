/*
 * Подсветка мыши — single-press KWin effect.
 * One animation is created on button-down only; button-up is ignored.
 * SPDX-License-Identifier: MIT
 */
#include <kwin/core/rendertarget.h>
#include <kwin/core/renderviewport.h>
#include <kwin/effect/effect.h>
#include <kwin/effect/effecthandler.h>
#include <kwin/opengl/glutils.h>
#include <kwin/opengl/gltexture.h>
#include <kwin/opengl/glvertexbuffer.h>

#include <KConfigGroup>

#include <QColor>
#include <QPainter>
#include <QVector2D>

#include <algorithm>
#include <array>
#include <cmath>
#include <deque>
#include <memory>
#include <vector>

namespace KWin
{

class PodsvetkaClickEffect : public Effect
{
    Q_OBJECT
    Q_PROPERTY(bool enabled READ isEnabled)
    Q_PROPERTY(int ringCount READ ringCount)
    Q_PROPERTY(int ringSize READ ringSize)
    Q_PROPERTY(int ringLife READ ringLife)

public:
    PodsvetkaClickEffect()
    {
        reconfigure(ReconfigureAll);
        connect(effects, &EffectsHandler::mouseChanged,
                this, &PodsvetkaClickEffect::slotMouseChanged);
    }

    void reconfigure(ReconfigureFlags) override
    {
        const KConfigGroup config(effects->config(), QStringLiteral("Effect-podsvetka-click"));
        m_colors[0] = config.readEntry("Color1", QColor(255, 82, 82));
        m_colors[1] = config.readEntry("Color2", QColor(255, 255, 255));
        m_colors[2] = config.readEntry("Color3", QColor(68, 138, 255));
        for (QColor &color : m_colors) {
            if (!color.isValid()) {
                color = QColor(Qt::white);
            }
        }
        m_lineWidth = std::clamp(config.readEntry("LineWidth", 2.0), 1.0, 12.0);
        m_ringLife = std::clamp(config.readEntry("RingLife", 420), 150, 3000);
        m_ringMaxSize = std::clamp(config.readEntry("RingSize", 24), 8, 300);
        m_ringCount = std::clamp(config.readEntry("RingCount", 1), 1, 4);
    }

    void prePaintScreen(ScreenPrePaintData &data, std::chrono::milliseconds presentTime) override
    {
        const int elapsed = m_lastPresentTime.count()
            ? int((presentTime - m_lastPresentTime).count())
            : 0;

        for (ClickEvent &click : m_clicks) {
            click.time += elapsed;
        }
        while (!m_clicks.empty() && m_clicks.front().time > m_ringLife) {
            m_clicks.pop_front();
        }

        m_lastPresentTime = isActive() ? presentTime : std::chrono::milliseconds::zero();
        effects->prePaintScreen(data, presentTime);
    }

    void paintScreen(const RenderTarget &renderTarget,
                     const RenderViewport &viewport,
                     int mask,
                     const QRegion &region,
                     Output *screen) override
    {
        effects->paintScreen(renderTarget, viewport, mask, region, screen);

        const bool glReady = effects->isOpenGLCompositing()
            && paintScreenSetupGl(renderTarget, viewport.projectionMatrix());

        for (const ClickEvent &click : m_clicks) {
            const float ringDistance = float(m_ringLife) / float(m_ringCount * 3);
            for (int ring = 0; ring < m_ringCount; ++ring) {
                const float alpha = (float(m_ringLife) - float(click.time) - ringDistance * ring)
                    / float(m_ringLife);
                const float radius = ((float(click.time) - ringDistance * ring)
                                      / float(m_ringLife)) * m_ringMaxSize;
                if (radius <= 0.0f || alpha <= 0.0f) {
                    continue;
                }
                QColor color = m_colors[click.button];
                color.setAlphaF(std::clamp(alpha, 0.0f, 1.0f));
                drawCircle(viewport, color, click.position.x(), click.position.y(), radius);
            }
        }

        if (glReady) {
            paintScreenFinishGl();
        }
    }

    void postPaintScreen() override
    {
        effects->postPaintScreen();
        repaint();
    }

    bool isActive() const override
    {
        return !m_clicks.empty();
    }

    bool isEnabled() const
    {
        return true;
    }

    int ringCount() const
    {
        return m_ringCount;
    }

    int ringSize() const
    {
        return m_ringMaxSize;
    }

    int ringLife() const
    {
        return m_ringLife;
    }

private Q_SLOTS:
    void slotMouseChanged(const QPointF &position,
                          const QPointF &,
                          Qt::MouseButtons buttons,
                          Qt::MouseButtons oldButtons,
                          Qt::KeyboardModifiers,
                          Qt::KeyboardModifiers)
    {
        // Intentionally react only to the rising edge (button-down).
        // KWin's built-in MouseClick effect also creates another animation
        // on button-up, which makes one physical click appear twice.
        static constexpr std::array<Qt::MouseButton, 3> buttonMasks = {
            Qt::LeftButton, Qt::MiddleButton, Qt::RightButton
        };

        for (int i = 0; i < int(buttonMasks.size()); ++i) {
            const Qt::MouseButtons button = buttonMasks[size_t(i)];
            const bool pressedNow = (buttons & button) && !(oldButtons & button);
            if (pressedNow) {
                m_clicks.push_back({i, position.toPoint(), 0});
                const int radius = int(m_ringMaxSize + m_lineWidth + 4);
                effects->addRepaint(QRect(position.toPoint() - QPoint(radius, radius),
                                          QSize(radius * 2, radius * 2)));
                break;
            }
        }
    }

private:
    struct ClickEvent
    {
        int button;
        QPoint position;
        int time;
    };

    void repaint()
    {
        if (m_clicks.empty()) {
            return;
        }
        QRegion dirty;
        const int radius = int(m_ringMaxSize + m_lineWidth + 4);
        for (const ClickEvent &click : m_clicks) {
            dirty += QRect(click.position - QPoint(radius, radius),
                           QSize(radius * 2, radius * 2));
        }
        effects->addRepaint(dirty);
    }

    void drawCircle(const RenderViewport &viewport,
                    const QColor &color,
                    float cx,
                    float cy,
                    float radius)
    {
        if (effects->isOpenGLCompositing()) {
            drawCircleGl(viewport, color, cx, cy, radius);
        } else if (effects->compositingType() == QPainterCompositing) {
            drawCircleQPainter(color, cx, cy, radius);
        }
    }

    void drawCircleGl(const RenderViewport &viewport,
                      const QColor &color,
                      float cx,
                      float cy,
                      float radius)
    {
        if (!m_aaTexture) {
            return;
        }

        // Draw a triangle-strip ring with a one-device-pixel alpha fringe.
        // Unlike GL_LINE_LOOP, this is antialiased in core OpenGL contexts
        // where legacy GL_LINE_SMOOTH is unavailable or ignored.
        static constexpr int segments = 256;
        const float scale = viewport.scale();
        const float scaledRadius = radius * scale;
        const float halfStroke = std::max(0.5f, m_lineWidth * scale * 0.5f);
        const float outerRadius = scaledRadius + halfStroke + 1.0f;
        const float innerRadius = std::max(0.0f, scaledRadius - halfStroke - 1.0f);
        const float centerX = cx * scale;
        const float centerY = cy * scale;

        std::vector<GLVertex2D> vertices;
        vertices.reserve((segments + 1) * 2);
        for (int i = 0; i <= segments; ++i) {
            const float angle = (2.0f * float(M_PI) * float(i)) / float(segments);
            const float x = std::cos(angle);
            const float y = std::sin(angle);

            GLVertex2D outer;
            outer.position = QVector2D(centerX + x * outerRadius,
                                       centerY + y * outerRadius);
            outer.texcoord = QVector2D(0.0f, 0.5f);
            vertices.push_back(outer);

            GLVertex2D inner;
            inner.position = QVector2D(centerX + x * innerRadius,
                                       centerY + y * innerRadius);
            inner.texcoord = QVector2D(1.0f, 0.5f);
            vertices.push_back(inner);
        }

        GLShader *shader = ShaderManager::instance()->getBoundShader();
        shader->setUniform(GLShader::Vec4Uniform::ModulationConstant,
                           QVector4D(color.redF(), color.greenF(),
                                     color.blueF(), color.alphaF()));

        glActiveTexture(GL_TEXTURE0);
        m_aaTexture->bind();
        GLVertexBuffer *buffer = GLVertexBuffer::streamingBuffer();
        buffer->reset();
        buffer->setVertices(vertices);
        buffer->render(GL_TRIANGLE_STRIP);
        m_aaTexture->unbind();
    }

    void drawCircleQPainter(const QColor &color, float cx, float cy, float radius)
    {
        QPainter *painter = effects->scenePainter();
        if (!painter) {
            return;
        }
        painter->save();
        painter->setRenderHint(QPainter::Antialiasing, true);
        QPen pen(color);
        pen.setWidthF(m_lineWidth);
        pen.setCapStyle(Qt::RoundCap);
        pen.setJoinStyle(Qt::RoundJoin);
        painter->setPen(pen);
        painter->drawEllipse(QRectF(cx - radius, cy - radius, radius * 2, radius * 2));
        painter->restore();
    }

    bool ensureAntialiasTexture()
    {
        if (m_aaTexture) {
            return true;
        }

        // A smooth 1D coverage profile is sampled across the ring thickness.
        // Linear texture filtering turns the edge into subpixel coverage.
        constexpr int textureWidth = 256;
        constexpr float edge = 0.18f;
        QImage image(textureWidth, 1, QImage::Format_ARGB32_Premultiplied);
        if (image.isNull()) {
            return false;
        }
        image.fill(Qt::transparent);

        for (int x = 0; x < textureWidth; ++x) {
            const float t = float(x) / float(textureWidth - 1);
            float coverage = 1.0f;
            if (t < edge) {
                const float u = t / edge;
                coverage = u * u * (3.0f - 2.0f * u);
            } else if (t > 1.0f - edge) {
                const float u = (1.0f - t) / edge;
                coverage = u * u * (3.0f - 2.0f * u);
            }
            const int alpha = int(std::lround(coverage * 255.0f));
            image.setPixel(x, 0, qRgba(alpha, alpha, alpha, alpha));
        }

        m_aaTexture = GLTexture::upload(image);
        if (!m_aaTexture) {
            return false;
        }
        m_aaTexture->setFilter(GL_LINEAR);
        m_aaTexture->setWrapMode(GL_CLAMP_TO_EDGE);
        return true;
    }

    bool paintScreenSetupGl(const RenderTarget &renderTarget,
                            const QMatrix4x4 &projectionMatrix)
    {
        if (!ensureAntialiasTexture()) {
            return false;
        }

        GLShader *shader = ShaderManager::instance()->pushShader(
            ShaderTrait::MapTexture | ShaderTrait::Modulate | ShaderTrait::TransformColorspace);
        shader->setUniform(GLShader::Mat4Uniform::ModelViewProjectionMatrix,
                           projectionMatrix);
        shader->setUniform(GLShader::IntUniform::Sampler, 0);
        shader->setColorspaceUniforms(ColorDescription::sRGB,
                                      renderTarget.colorDescription(),
                                      RenderingIntent::Perceptual);
        glEnable(GL_BLEND);
        glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
        return true;
    }

    void paintScreenFinishGl()
    {
        glDisable(GL_BLEND);
        ShaderManager::instance()->popShader();
    }

    QColor m_colors[3] = {
        QColor(255, 82, 82), QColor(255, 255, 255), QColor(68, 138, 255)
    };
    int m_ringCount = 1;
    float m_lineWidth = 2.0f;
    int m_ringLife = 420;
    float m_ringMaxSize = 24.0f;
    std::chrono::milliseconds m_lastPresentTime = std::chrono::milliseconds::zero();
    std::deque<ClickEvent> m_clicks;
    std::unique_ptr<GLTexture> m_aaTexture;
};

KWIN_EFFECT_FACTORY(PodsvetkaClickEffect, "podsvetka_click.json")

} // namespace KWin

#include "podsvetka_click.moc"
