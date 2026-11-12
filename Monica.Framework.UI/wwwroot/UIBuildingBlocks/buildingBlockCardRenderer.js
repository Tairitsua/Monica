/**
 * Building block card renderer
 * Complex node cards dedicated to rendering building blocks
 * 
 * @module buildingBlockCardRenderer
 */

const MUTED_INK = 'var(--building-blocks-muted-ink, color-mix(in srgb, var(--mud-palette-text-secondary) 85%, var(--mud-palette-text-primary)))';
const ACCENT_INK = 'var(--building-blocks-accent-ink, color-mix(in srgb, var(--mud-palette-primary) 80%, var(--mud-palette-text-primary)))';
const CHIP_COLOR_MAP = Object.fromEntries(
    ['primary', 'secondary', 'tertiary', 'info', 'success', 'warning', 'error', 'dark'].map(role => [role, {
        background: 'color-mix(in srgb, var(--mud-palette-' + role + ') 8%, var(--mud-palette-surface))',
        border: 'color-mix(in srgb, var(--mud-palette-' + role + ') 25%, var(--mud-palette-divider))',
        text: 'color-mix(in srgb, var(--mud-palette-' + role + ') 65%, var(--mud-palette-text-primary))'
    }]));
CHIP_COLOR_MAP.default = { background: 'var(--mud-palette-surface)', border: 'var(--mud-palette-divider)', text: MUTED_INK };
CHIP_COLOR_MAP.dark.text = MUTED_INK;

function getCardStyle() {
    return {
        backgroundColor: 'var(--mud-palette-surface)',
        strokeColor: 'var(--mud-palette-divider)', strokeWidth: 1.8,
        headerColor: 'color-mix(in srgb, var(--mud-palette-primary) 7%, var(--mud-palette-surface))',
        headerTextColor: ACCENT_INK, contentTextColor: 'var(--mud-palette-text-primary)',
        footerColor: 'color-mix(in srgb, var(--mud-palette-text-primary) 3%, var(--mud-palette-surface))'
    };
}

/**
 * Building block card renderer class
 * Provides modern card-style node drawing with three-layer layout
 */
export class BuildingBlockCardRenderer {
    constructor(isDarkMode = false, sizeConfig = null) {
        this.isDarkMode = isDarkMode;
        this.style = getCardStyle();
        
        // Card layout configuration
        this.config = {
            minWidth: sizeConfig?.minWidth || 180,
            maxWidth: sizeConfig?.maxWidth || 280,
            padding: 12,
            borderRadius: 6,
            
            // Title bar configuration
            header: {
                height: 36,
                padding: 12,
                fontSize: '13px',
                fontWeight: '500'
            },
            
            // Content area configuration (display method information, etc.)
            content: {
                minHeight: 60,
                padding: 12,
                fontSize: '13px',
                lineHeight: 1.4,
                itemSpacing: 8
            },
            
            // Status bar configuration (showing Chips)
            footer: {
                height: 32,
                padding: 8,
                chipHeight: 20,
                chipPadding: 8,
                chipFontSize: '11px',
                chipBorderRadius: 4
            }
        };
    }
    
    /**
     * Calculate building block card size
     * @param {Object} nodeData - node data
     * @returns {Object} size information {width, height}
     */
    calculateCardSize(nodeData) {
        const { config } = this;
        const contentItems = nodeData.metadata || [];
        
        // Calculate title width (including icon space)
        const titleWidth = this.estimateTextWidth(nodeData.title, config.header.fontSize, config.header.fontWeight) + 80;
        
        // Calculate content width - special handling information
        let maxContentWidth = config.minWidth;
        if (contentItems.length > 0) {
            contentItems.forEach(item => {
                const keyWidth = this.estimateTextWidth(`${item.key}: `, config.content.fontSize, '500');
                const valueWidth = this.estimateTextWidth(item.value, config.content.fontSize);
                
                // For method signatures, limit the maximum width
                let effectiveValueWidth = valueWidth;
                if (item.kind === 'method' && valueWidth > 200) {
                    effectiveValueWidth = Math.min(valueWidth, 250);
                }
                
                const totalWidth = keyWidth + effectiveValueWidth + config.content.padding * 2;
                maxContentWidth = Math.max(maxContentWidth, totalWidth);
            });
        }
        
        // Dynamically adjust the maximum width based on content complexity
        let dynamicMaxWidth = config.maxWidth;
        if (contentItems.length > 3) {
            dynamicMaxWidth = Math.min(config.maxWidth * 1.5, 400);
        }
        
        const width = Math.max(config.minWidth, Math.min(dynamicMaxWidth, Math.max(titleWidth, maxContentWidth)));
        
        // Calculate height
        const contentHeight = contentItems.length > 0 
            ? Math.max(
                config.content.minHeight,
                contentItems.length * (parseFloat(config.content.fontSize) * config.content.lineHeight + config.content.itemSpacing) + config.content.padding * 2
              )
            : config.content.minHeight;
        
        const footerHeight = this.calculateFooterHeight(nodeData, width);
        const height = config.header.height + contentHeight + footerHeight;
        
        return { width, height };
    }
    
    /**
     * Estimate text width
     */
    estimateTextWidth(text, fontSize, fontWeight = 'normal') {
        if (!this._measureCanvas) {
            this._measureCanvas = document.createElement('canvas');
            this._measureContext = this._measureCanvas.getContext('2d');
        }
        
        this._measureContext.font = `${fontWeight} ${fontSize} ${this.fontFamily || getComputedStyle(document.documentElement).fontFamily}`;
        const metrics = this._measureContext.measureText(text);
        return metrics.width;
    }
    
    /**
     * Calculate status bar height (supports multi-line chips)
     */
    calculateFooterHeight(nodeData, width) {
        const { config } = this;
        const chips = nodeData.chips || [];
        
        if (chips.length === 0) {
            return config.footer.height;
        }
        
        const availableWidth = width - config.footer.padding * 2;
        let currentRowWidth = 0;
        let rowCount = 1;
        const chipSpacing = 6;
        
        chips.forEach((chip, index) => {
            const hasIcon = chip.icon && chip.icon.trim() !== '';
            const chipWidth = this.estimateChipWidth(chip.text, hasIcon, availableWidth);
            
            if (index > 0) {
                currentRowWidth += chipSpacing;
            }
            
            if (currentRowWidth + chipWidth > availableWidth && index > 0) {
                rowCount++;
                currentRowWidth = chipWidth;
            } else {
                currentRowWidth += chipWidth;
            }
        });
        
        const chipsTotalHeight = rowCount * config.footer.chipHeight + (rowCount - 1) * 6;
        const verticalPadding = config.footer.padding * 2;
        
        return Math.max(config.footer.height, chipsTotalHeight + verticalPadding);
    }
    
    /**
     * Draw building block cards
     */
    drawCard(nodeElement, nodeData) {
        const { width, height } = this.calculateCardSize(nodeData);
        const { config, style } = this;
        
        nodeElement.selectAll('*').remove();
        
        const card = nodeElement.append('g')
            .attr('class', 'building-block-card')
            .attr('data-alert-level', nodeData.alertLevel || 'none');
        
        // Draw card background
        const cardBackground = card.append('rect')
            .attr('class', 'card-background')
            .attr('width', width)
            .attr('height', height)
            .attr('x', -width / 2)
            .attr('y', -height / 2)
            .attr('rx', config.borderRadius)
            .attr('ry', config.borderRadius)
            .attr('fill', style.backgroundColor)
            .attr('stroke', style.strokeColor)
            .attr('stroke-width', style.strokeWidth)
            .style('cursor', 'pointer');
        
        // Draw the three card sections.
        this.drawHeader(card, nodeData, width, height, config.header.height);
        
        const contentItems = nodeData.metadata || [];
        const contentHeight = height - config.header.height - this.calculateFooterHeight(nodeData, width);
        const contentStartY = -height / 2 + config.header.height;
        this.drawContent(card, nodeData, contentItems, width, contentHeight, contentStartY);
        
        const footerHeight = this.calculateFooterHeight(nodeData, width);
        const footerY = height / 2 - footerHeight;
        this.drawFooter(card, nodeData, width, footerHeight, footerY);
        
        // Store size information
        nodeData._cardSize = { width, height };
        
        return card;
    }
    
    /**
     * Draw title bar
     */
    drawHeader(card, nodeData, width, cardHeight, headerHeight) {
        const { config, style } = this;
        const headerY = -cardHeight / 2;
        
        // title bar background
        card.append('rect')
            .attr('class', 'card-header')
            .attr('width', width - style.strokeWidth * 2)
            .attr('height', headerHeight)
            .attr('x', -width / 2 + style.strokeWidth)
            .attr('y', headerY + style.strokeWidth)
            .attr('fill', style.headerColor);
        
        // Draw icons and titles
        this.drawHeaderContent(card, nodeData, width, headerY, headerHeight);
    }
    
    /**
     * Draw the content area and show metadata such as methods.
     */
    drawContent(card, nodeData, contentItems, width, height, startY) {
        const { config, style } = this;
        
        if (contentItems.length === 0) {
            card.append('text')
                .attr('class', 'content-placeholder')
                .attr('x', 0)
                .attr('y', startY + height / 2)
                .attr('text-anchor', 'middle')
                .attr('fill', style.contentTextColor)
                .style('font-size', config.content.fontSize)
                .attr('fill', MUTED_INK)
                .style('pointer-events', 'none')
                .text(nodeData.noMetadataText || '');
            return;
        }
        
        const firstItemY = startY + config.content.padding + parseFloat(config.content.fontSize);
        const lineHeight = parseFloat(config.content.fontSize) * config.content.lineHeight + config.content.itemSpacing;
        
        contentItems.forEach((item, index) => {
            const y = firstItemY + index * lineHeight;
            
            const keyText = this.fitText(`${item.key}:`, (width - config.content.padding * 2) * 0.4, config.content.fontSize, '500');
            const keyWidth = this.estimateTextWidth(keyText, config.content.fontSize, '500');
            
            // Key name
            card.append('text')
                .attr('class', 'content-key')
                .attr('x', -width / 2 + config.content.padding)
                .attr('y', y)
                .attr('text-anchor', 'start')
                .attr('fill', style.contentTextColor)
                .style('font-size', config.content.fontSize)
                .style('font-weight', '500')
                .attr('fill', MUTED_INK)
                .style('pointer-events', 'none')
                .text(keyText);
            
            // value (smart truncation)
            const valueStartX = -width / 2 + config.content.padding + keyWidth + 5;
            const displayValue = this.fitText(item.value, width / 2 - config.content.padding - valueStartX, config.content.fontSize);

            const valueText = card.append('text')
                .attr('class', 'content-value')
                .attr('x', valueStartX)
                .attr('y', y)
                .attr('text-anchor', 'start')
                .attr('fill', style.contentTextColor)
                .style('font-size', config.content.fontSize)
                .style('pointer-events', 'none')
                .text(displayValue);
            
            // Add tooltip to display complete content
            valueText.append('title').text(item.value);
        });
    }
    
    /**
     * Draw status bar - display various status Chips
     */
    drawFooter(card, nodeData, width, footerHeight, y) {
        const { config, style } = this;
        
        // status bar background
        card.append('rect')
            .attr('class', 'card-footer')
            .attr('width', width - style.strokeWidth * 2)
            .attr('height', footerHeight - style.strokeWidth)
            .attr('x', -width / 2 + style.strokeWidth)
            .attr('y', y)
            .attr('fill', style.footerColor);
        
        // Draw chips
        this.drawChipsLayout(card, nodeData.chips || [], width, footerHeight, y);
    }
    
    /**
     * Calculate row grouping of chips
     */
    calculateChipRows(chips, availableWidth, chipSpacing) {
        const rows = [];
        let currentRow = [];
        let currentRowWidth = 0;
        
        chips.forEach((chip, index) => {
            const hasIcon = chip.icon && chip.icon.trim() !== '';
            const chipWidth = this.estimateChipWidth(chip.text, hasIcon, availableWidth);
            const needSpacing = currentRow.length > 0 ? chipSpacing : 0;
            
            if (currentRow.length > 0 && currentRowWidth + needSpacing + chipWidth > availableWidth) {
                rows.push(currentRow);
                currentRow = [chip];
                currentRowWidth = chipWidth;
            } else {
                currentRow.push(chip);
                currentRowWidth += needSpacing + chipWidth;
            }
        });
        
        if (currentRow.length > 0) {
            rows.push(currentRow);
        }
        
        return rows;
    }
    
    /**
     * Draw chips layout
     */
    drawChipsLayout(container, chips, width, height, startY) {
        const { config } = this;
        
        if (!chips || chips.length === 0) return;
        
        const availableWidth = width - config.footer.padding * 2;
        const chipSpacing = 6;
        const rowSpacing = 6;
        
        const rows = this.calculateChipRows(chips, availableWidth, chipSpacing);
        const totalRowsHeight = rows.length * config.footer.chipHeight + (rows.length - 1) * rowSpacing;
        const verticalCenterOffset = (height - totalRowsHeight) / 2;
        
        rows.forEach((row, rowIndex) => {
            const rowY = startY + verticalCenterOffset + config.footer.chipHeight / 2 + 
                        rowIndex * (config.footer.chipHeight + rowSpacing);
            
            const rowTotalWidth = row.reduce((total, chip, index) => {
                const hasIcon = chip.icon && chip.icon.trim() !== '';
                return total + this.estimateChipWidth(chip.text, hasIcon, availableWidth) + (index > 0 ? chipSpacing : 0);
            }, 0);
            
            let currentX = width / 2 - config.footer.padding - rowTotalWidth;
            
            row.forEach((chip, chipIndex) => {
                const hasIcon = chip.icon && chip.icon.trim() !== '';
                const chipWidth = this.estimateChipWidth(chip.text, hasIcon, availableWidth);
                
                if (chipIndex > 0) {
                    currentX += chipSpacing;
                }
                
                this.drawChip(container, chip, currentX, rowY, availableWidth);
                currentX += chipWidth;
            });
        });
    }
    
    /**
     * Draw a single Chip
     */
    drawChip(container, chipData, x, y, availableWidth) {
        const { config } = this;
        const hasIcon = chipData.icon && chipData.icon.trim() !== '';
        const iconSize = 14;
        const iconPadding = hasIcon ? 4 : 0;
        
        const chipWidth = this.estimateChipWidth(chipData.text, hasIcon, availableWidth);
        const colors = this.getChipColors(chipData.color);
        
        // Chip background
        container.append('rect')
            .attr('class', `chip chip-${chipData.color}`)
            .attr('width', chipWidth)
            .attr('height', config.footer.chipHeight)
            .attr('x', x)
            .attr('y', y - config.footer.chipHeight / 2)
            .attr('rx', config.footer.chipBorderRadius)
            .attr('ry', config.footer.chipBorderRadius)
            .attr('fill', colors.background)
            .attr('stroke', colors.border)
            .attr('stroke-width', 1)
            .style('opacity', 1);
        
        let textX = x + chipWidth / 2;
        
        // draw icon
        if (hasIcon) {
            const iconX = x + config.footer.chipPadding;
            this.drawChipIcon(container, chipData.icon, iconX, y, iconSize, colors.text);
            textX = iconX + iconSize + iconPadding;
        }
        
        // Chip text
        const text = container.append('text')
            .attr('class', 'chip-text')
            .attr('x', textX)
            .attr('y', y + 3)
            .attr('text-anchor', hasIcon ? 'start' : 'middle')
            .attr('fill', colors.text)
            .style('font-size', config.footer.chipFontSize)
            .style('font-weight', '500')
            .style('pointer-events', 'none')
            .text(this.fitText(chipData.text, chipWidth - config.footer.chipPadding * 2 - (hasIcon ? iconSize + iconPadding : 0), config.footer.chipFontSize, '500'));
        text.append('title').text(chipData.text);
    }
    
    /**
     * Get chip color configuration
     */
    getChipColors(colorName) {
        return CHIP_COLOR_MAP[colorName] || CHIP_COLOR_MAP.default;
    }
    
    /**
     * Draw Chip Icon
     */
    drawChipIcon(container, iconSvg, x, y, size, color) {
        const iconGroup = container.append('g')
            .attr('class', 'chip-icon')
            .attr('transform', `translate(${x + size/2}, ${y}) scale(${size/24}) translate(-12, -12)`);
        
        this.renderMudBlazorIcon(iconGroup, iconSvg, color);
    }
    
    /**
     * Estimate Chip Width
     */
    estimateChipWidth(text, hasIcon = false, availableWidth = Infinity) {
        const { config } = this;
        const textWidth = this.estimateTextWidth(text, config.footer.chipFontSize, '500');
        
        let width = config.footer.chipPadding;
        if (hasIcon) {
            width += 14 + 4; // iconSize + iconPadding
        }
        width += textWidth;
        width += config.footer.chipPadding;
        
        return Math.min(width, availableWidth);
    }
    
    /**
     * Draw title bar content
     */
    drawHeaderContent(container, nodeData, width, headerY, headerHeight) {
        const { config, style } = this;
        const leftPadding = config.header.padding;
        const iconSize = 16;
        const iconTextSpacing = 8;
        
        let currentX = -width / 2 + leftPadding;
        const centerY = headerY + style.strokeWidth + headerHeight / 2;
        
        // draw icon
        if (nodeData.icon) {
            this.drawHeaderIcon(container, nodeData.icon, currentX, centerY, iconSize);
            currentX += iconSize + iconTextSpacing;
        }
        
        // draw title
        container.append('text')
            .attr('class', 'card-title')
            .attr('x', currentX)
            .attr('y', centerY + 1)
            .attr('text-anchor', 'start')
            .attr('dominant-baseline', 'middle')
            .attr('fill', style.headerTextColor)
            .style('font-size', config.header.fontSize)
            .style('font-weight', config.header.fontWeight)
            .style('pointer-events', 'none')
            .text(this.fitText(nodeData.title, width - config.header.padding * 2 - (nodeData.icon ? iconSize + iconTextSpacing : 0), config.header.fontSize, config.header.fontWeight));
    }
    
    /**
     * Draw title bar icon
     */
    drawHeaderIcon(container, iconSvg, x, y, size) {
        const { style } = this;
        
        const iconGroup = container.append('g')
            .attr('class', 'header-icon')
            .attr('transform', `translate(${x + size/2}, ${y}) scale(${size/24}) translate(-12, -12)`);
        
        this.renderMudBlazorIcon(iconGroup, iconSvg, style.headerTextColor);
    }
    
    /**
     * Rendering MudBlazor SVG icon
     */
    renderMudBlazorIcon(container, iconSvg, color) {
        if (!iconSvg || iconSvg.trim() === '') {
            container.append('circle')
                .attr('cx', 12)
                .attr('cy', 12)
                .attr('r', 2)
                .attr('fill', color)
                .style('pointer-events', 'none');
            return;
        }
        
        try {
            const parser = new DOMParser();
            const svgDoc = parser.parseFromString(`<svg xmlns="http://www.w3.org/2000/svg">${iconSvg}</svg>`, 'image/svg+xml');
            const paths = svgDoc.querySelectorAll('path:not([fill="none"])');
            
            paths.forEach(path => {
                const pathData = path.getAttribute('d');
                if (pathData) {
                    container.append('path')
                        .attr('d', pathData)
                        .attr('fill', color)
                        .style('pointer-events', 'none');
                }
            });
            
            if (paths.length === 0) {
                container.append('circle')
                    .attr('cx', 12)
                    .attr('cy', 12)
                    .attr('r', 3)
                    .attr('fill', color)
                    .style('pointer-events', 'none');
            }
        } catch (error) {
            console.warn('Failed to parse MudBlazor icon:', error);
            container.append('circle')
                .attr('cx', 12)
                .attr('cy', 12)
                .attr('r', 3)
                .attr('fill', color)
                .style('pointer-events', 'none');
        }
    }
    
    setFontFamily(family) {
        this.fontFamily = family;
    }

    fitText(value, availableWidth, fontSize, fontWeight = 'normal') {
        const text = String(value ?? '');
        if (this.estimateTextWidth(text, fontSize, fontWeight) <= availableWidth) return text;
        const characters = Array.from(text);
        let lower = 0, upper = characters.length;
        while (lower < upper) {
            const middle = Math.ceil((lower + upper) / 2);
            if (this.estimateTextWidth(characters.slice(0, middle).join('') + '…', fontSize, fontWeight) <= availableWidth) lower = middle;
            else upper = middle - 1;
        }
        return characters.slice(0, lower).join('') + '…';
    }

    /**
     * Update theme
     */
    updateTheme(isDarkMode) {
        this.isDarkMode = isDarkMode;
        this.style = getCardStyle();
    }
}

/**
 * Create a building block card renderer instance
 */
export function createBuildingBlockCardRenderer(isDarkMode = false, sizeConfig = null) {
    return new BuildingBlockCardRenderer(isDarkMode, sizeConfig);
}
